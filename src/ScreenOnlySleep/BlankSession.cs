using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static ScreenOnlySleep.NativeMethods;

namespace ScreenOnlySleep
{
  internal sealed class SessionOptions
  {
    public Hotkey Hotkey = Hotkey.Default;
    public bool UseDdc = true;
    public double MaxHours = Settings.DefaultAutoReleaseHours;
    public int TestSeconds;   // > 0: release automatically after this many seconds
  }

  // One period of blanked screens. Create, Start and everything else on the UI thread (a WinForms message loop).
  //
  // - Every monitor is covered by a topmost black window that is click-through (LAYERED | TRANSPARENT, alpha 255),
  //   never takes focus (NOACTIVATE, TOOLWINDOW) and is excluded from screen capture (WDA_EXCLUDEFROMCAPTURE).
  //   Windows itself still thinks the display is on, so moving the mouse does not light it up.
  // - Low-level keyboard and mouse hooks run on their own thread (so slow DDC calls can never time them out).
  //   A real key down or mouse button down releases; moves, wheel and injected input do not.
  //   The releasing input is swallowed from its down to the matching up.
  internal sealed class BlankSession
  {
    const string BlackClass = "ScreenOnlySleepBlack";
    const int WS_EX_BLACK = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
    const uint WM_SIM_KEY = WM_APP + 1, WM_SIM_MOUSE = WM_APP + 2;
    const int MaxWinLog = 30;

    static readonly WndProc blackProc = BlackWndProc;   // kept alive for the native side
    static bool classRegistered;

    readonly SessionOptions opt;
    readonly Logger log;
    readonly DdcController ddc;
    readonly ReleaseTracker tracker = new ReleaseTracker();
    readonly SkipRecorder skips = new SkipRecorder();
    readonly ArmGate armGate;
    readonly List<IntPtr> blackWnds = new List<IntPtr>();
    readonly Stopwatch clock = new Stopwatch();
    readonly HookProc kbProc, msProc;
    readonly WinEventProc winProc;
    readonly int ownPid = Process.GetCurrentProcess().Id;
    System.Windows.Forms.Timer timer;
    Thread hookThread;
    uint hookThreadId;
    IntPtr kbHook, msHook, fgHook, showHook;
    bool started, finishing, ended;
    Stopwatch finishClock;
    int tick;

    // window-change log (UI thread)
    int winLogCount, winLogDropped;
    string lastLoggedWnd;
    int lastLoggedTick;

    // input log: queued on the hook thread, written on the UI thread
    sealed class InputEv { public DateTime Time; public string Text; public uint Pid; }
    readonly object pendingLock = new object();
    readonly Queue<InputEv> pending = new Queue<InputEv>();
    readonly Dictionary<uint, string> procNames = new Dictionary<uint, string>();

    // real mouse moves seen while watching (hook thread only); logged with the releasing input
    int lastRealMoveTick, realMoveCount;
    bool anyRealMove;
    readonly int[] recentMoves = new int[256];
    int recentMovesPos;

    public event Action<BlankSession> Ended;
    public string Reason { get { return tracker.Reason; } }
    public TriggerKind Triggered { get { return tracker.Triggered; } }
    public bool IsFinishing { get { return finishing; } }

    public BlankSession(SessionOptions options, Logger log, DdcController ddc)
    {
      opt = options;
      this.log = log;
      this.ddc = ddc;
      var keys = new List<int>(opt.Hotkey.ReleaseWaitKeys());
      keys.AddRange(Hotkey.ExtraReleaseWaitKeys);
      armGate = new ArmGate(keys, IsKeyDown);
      kbProc = KeyboardHook;
      msProc = MouseHook;
      winProc = OnWinEvent;
    }

    public void Start()
    {
      if (started) throw new InvalidOperationException("already started");
      started = true;
      log.Log("session start hotkey=" + opt.Hotkey + " ddc=" + opt.UseDdc + " maxHours=" + opt.MaxHours + " testSeconds=" + opt.TestSeconds + " pid=" + ownPid);
      clock.Start();
      try
      {
        RegisterClass();
        BuildBlackWindows();
        if (opt.UseDdc) ddc.EnqueueApply();
        StartHookThread();
        StartWinEventHooks();
        timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (s, e) => OnTick();
        timer.Start();
      }
      catch (Exception ex)
      {
        log.Log("session start failed: " + ex);
        tracker.TriggerByTimer("error");
        Finish();
      }
    }

    // Ends the session now (app exit). DDC is restored by the caller.
    public void Abort(string why)
    {
      if (ended) return;
      tracker.TriggerByTimer(why);
      Finish();
    }

    // Test seam: feeds a non-injected event into the same code path the hooks use, on the hook thread.
    public void SimulateRealKey(uint vk)
    {
      PostThreadMessage(hookThreadId, WM_SIM_KEY, new IntPtr(vk), new IntPtr(InputFilter.WM_KEYDOWN));
      PostThreadMessage(hookThreadId, WM_SIM_KEY, new IntPtr(vk), new IntPtr(InputFilter.WM_KEYUP));
    }

    public void SimulateRealClick()
    {
      PostThreadMessage(hookThreadId, WM_SIM_MOUSE, new IntPtr(InputFilter.WM_LBUTTONDOWN), IntPtr.Zero);
      PostThreadMessage(hookThreadId, WM_SIM_MOUSE, new IntPtr(InputFilter.WM_LBUTTONUP), IntPtr.Zero);
    }

    public void OnDisplayChange()
    {
      if (!started || finishing) return;
      log.Log("display configuration changed");
      BuildBlackWindows();
    }

    // ---------- black windows ----------
    static void RegisterClass()
    {
      if (classRegistered) return;
      var wc = new WNDCLASS
      {
        lpfnWndProc = blackProc,
        hInstance = GetModuleHandle(IntPtr.Zero),
        hbrBackground = GetStockObject(4 /* BLACK_BRUSH */),
        lpszClassName = BlackClass,
      };
      if (NativeMethods.RegisterClass(ref wc) == 0) throw new InvalidOperationException("RegisterClass failed: " + Marshal.GetLastWin32Error());
      classRegistered = true;
    }

    static IntPtr BlackWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
      if (msg == 0x0021) return new IntPtr(3);   // WM_MOUSEACTIVATE -> MA_NOACTIVATE (mouse goes through anyway)
      if (msg == 0x0010) return IntPtr.Zero;     // ignore WM_CLOSE
      return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    void DestroyBlackWindows()
    {
      foreach (var h in blackWnds) DestroyWindow(h);
      blackWnds.Clear();
    }

    void BuildBlackWindows()
    {
      DestroyBlackWindows();
      var mons = Monitors.Enumerate(false);
      int affOk = 0;
      foreach (var m in mons)
      {
        var r = m.Rect;
        // Created hidden; shown without activation once its attributes are set, so it never flashes into capture or focus.
        IntPtr h = CreateWindowEx(WS_EX_BLACK, BlackClass, "ScreenOnlySleep", WS_POPUP,
          r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
        if (h == IntPtr.Zero) { log.Log("CreateWindowEx failed: " + Marshal.GetLastWin32Error()); continue; }
        if (!SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA)) log.Log("SetLayeredWindowAttributes failed: " + Marshal.GetLastWin32Error());
        if (SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE)) affOk++;
        else log.Log("SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed: " + Marshal.GetLastWin32Error() + " (captures will show the black window; not falling back to WDA_MONITOR)");
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        blackWnds.Add(h);
      }
      KeepTopmost();
      log.Log("black windows " + blackWnds.Count + " (monitors " + mons.Count + ", click-through, excluded from capture " + affOk + ")");
    }

    void KeepTopmost()
    {
      foreach (var h in blackWnds) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    void HideBlackWindows() { foreach (var h in blackWnds) ShowWindow(h, 0); }

    // ---------- staying on top: when the foreground changes or a window appears, go back on top without taking focus ----------
    void StartWinEventHooks()
    {
      fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winProc, 0, 0, 0 /* OUTOFCONTEXT */);
      showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, winProc, 0, 0, 0);
      if (fgHook == IntPtr.Zero || showHook == IntPtr.Zero) log.Log("SetWinEventHook failed (staying on top by the periodic refresh only)");
    }

    void StopWinEventHooks()
    {
      if (fgHook != IntPtr.Zero) UnhookWinEvent(fgHook);
      if (showHook != IntPtr.Zero) UnhookWinEvent(showHook);
      fgHook = showHook = IntPtr.Zero;
    }

    string WindowText(IntPtr hwnd)
    {
      uint pid;
      GetWindowThreadProcessId(hwnd, out pid);
      var cls = new StringBuilder(128);
      GetClassName(hwnd, cls, 128);
      return ProcName(pid) + "(" + pid + ") class=" + cls;
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
      try
      {
        if (finishing || hwnd == IntPtr.Zero || idObject != 0 /* OBJID_WINDOW */ || blackWnds.Contains(hwnd)) return;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;   // ignore child windows
        if (ev == EVENT_OBJECT_SHOW && !IsWindowVisible(hwnd)) return;
        bool topmost = (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        KeepTopmost();
        // log every foreground change, and shown windows only when they are topmost
        if (ev == EVENT_SYSTEM_FOREGROUND || topmost)
        {
          string text = (ev == EVENT_SYSTEM_FOREGROUND ? "foreground changed " : "topmost window shown ") + WindowText(hwnd) + (topmost ? " TOPMOST" : "");
          int now = Environment.TickCount;
          if (text == lastLoggedWnd && now - lastLoggedTick < 2000) { lastLoggedTick = now; return; }
          lastLoggedWnd = text;
          lastLoggedTick = now;
          if (winLogCount < MaxWinLog) { winLogCount++; log.Log(text + " -> black windows back on top"); }
          else winLogDropped++;
        }
      }
      catch (Exception ex) { log.Log("winevent error: " + ex.Message); }
    }

    // ---------- timer ----------
    void OnTick()
    {
      try
      {
        tick++;
        if (!tracker.Armed && armGate.Poll())
        {
          tracker.Arm();
          log.Log("watching for release (hotkey and buttons are up)");
        }
        double sec = clock.Elapsed.TotalSeconds;
        if (opt.TestSeconds > 0 && sec >= opt.TestSeconds) tracker.TriggerByTimer("test");
        if (sec >= opt.MaxHours * 3600) tracker.TriggerByTimer("timeout");

        var t = tracker.Triggered;
        if (t != TriggerKind.None && !finishing) BeginFinish(t);
        if (finishing)
        {
          // keep the hooks until the releasing input's ups have been swallowed (at most 2.5 s)
          bool inputDone = t != TriggerKind.Input || tracker.ReleaseDone || finishClock.ElapsedMilliseconds > 2500;
          if (inputDone)
          {
            if (t == TriggerKind.Input && !tracker.ReleaseDone) log.Log("timed out waiting for the releasing input to come up");
            Finish();
          }
          return;
        }
        if (tick % 5 == 0) KeepTopmost();   // every 250 ms as well, in case an event was missed
        if (tick % 10 == 0) FlushInputLog();
      }
      catch (Exception ex) { log.Log("tick error: " + ex); }
    }

    void BeginFinish(TriggerKind t)
    {
      finishing = true;
      finishClock = Stopwatch.StartNew();
      FlushInputLog();
      log.Log("release: " + (t == TriggerKind.Input ? "input(" + tracker.Reason + ")" : tracker.Reason));
      HideBlackWindows();
      ddc.EnqueueRestore("release", true);
    }

    void Finish()
    {
      if (ended) return;
      if (!finishing) BeginFinish(tracker.Triggered);
      ended = true;
      if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
      StopWinEventHooks();
      StopHookThread();
      DestroyBlackWindows();
      FlushInputLog();
      if (winLogDropped > 0) log.Log("window changes omitted from the log: " + winLogDropped);
      log.Log(skips.Summary());
      log.Log("session end reason=" + (tracker.Reason ?? "?"));
      var h = Ended;
      if (h != null) h(this);
    }

    // ---------- input log ----------
    static uint ForegroundPid()
    {
      uint pid = 0;
      try { GetWindowThreadProcessId(GetForegroundWindow(), out pid); } catch { }
      return pid;
    }

    void Enqueue(string text)
    {
      var ev = new InputEv { Time = DateTime.Now, Text = text, Pid = ForegroundPid() };
      lock (pendingLock) pending.Enqueue(ev);
    }

    void RecordSkip(string sig, Func<string> detail)
    {
      if (skips.Record(sig)) Enqueue("ignored " + detail());
    }

    string ProcName(uint pid)
    {
      if (pid == 0) return "(none)";
      if (pid == ownPid) return "(self)";
      string n;
      if (procNames.TryGetValue(pid, out n)) return n;
      try { using (var p = Process.GetProcessById((int)pid)) n = p.ProcessName; } catch { n = "?"; }
      procNames[pid] = n;
      return n;
    }

    void FlushInputLog()
    {
      List<InputEv> items;
      lock (pendingLock) { items = new List<InputEv>(pending); pending.Clear(); }
      foreach (var e in items) log.Log("input " + e.Time.ToString("HH:mm:ss.fff") + " " + e.Text + " fg=" + ProcName(e.Pid) + "(" + e.Pid + ")");
    }

    string RecentRealInputText()
    {
      int now = Environment.TickCount, n = 0;
      foreach (var t in recentMoves) if (t != 0 && now - t <= 3000) n++;
      return "realMovesLast3s=" + n + " lastRealMove=" + (anyRealMove ? ((now - lastRealMoveTick) / 1000.0).ToString("F1") + "s ago" : "none")
        + " realMovesWhileWatching=" + realMoveCount;
    }

    // ---------- hooks (hook thread) ----------
    void StartHookThread()
    {
      var ready = new ManualResetEvent(false);
      string error = null;
      hookThread = new Thread(() =>
      {
        MSG m;
        PeekMessage(out m, IntPtr.Zero, 0, 0, 0);   // create the thread's message queue
        hookThreadId = GetCurrentThreadId();
        IntPtr mod = GetModuleHandle(IntPtr.Zero);
        kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, kbProc, mod, 0);
        msHook = SetWindowsHookEx(WH_MOUSE_LL, msProc, mod, 0);
        if (kbHook == IntPtr.Zero || msHook == IntPtr.Zero) error = "SetWindowsHookEx failed: " + Marshal.GetLastWin32Error();
        ready.Set();
        if (error == null)
        {
          while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
          {
            if (m.hwnd == IntPtr.Zero && m.message == WM_SIM_KEY) { HandleKey((uint)m.wParam.ToInt64(), 0, 0, (int)m.lParam.ToInt64(), 0, true); continue; }
            if (m.hwnd == IntPtr.Zero && m.message == WM_SIM_MOUSE) { HandleMouse((int)m.wParam.ToInt64(), 0, 0, 0, 0, 0, true); continue; }
            TranslateMessage(ref m);
            DispatchMessage(ref m);
          }
        }
        if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook);
        if (msHook != IntPtr.Zero) UnhookWindowsHookEx(msHook);
        kbHook = msHook = IntPtr.Zero;
      });
      hookThread.IsBackground = true;
      hookThread.Name = "InputHooks";
      hookThread.Start();
      ready.WaitOne();
      if (error != null) throw new InvalidOperationException(error);
    }

    void StopHookThread()
    {
      if (hookThread == null) return;
      PostThreadMessage(hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
      hookThread.Join(2000);
      hookThread = null;
    }

    IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
      if (nCode >= 0)
      {
        var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
        if (HandleKey(k.vkCode, k.scanCode, k.flags, wParam.ToInt32(), k.dwExtraInfo.ToUInt64(), false)) return new IntPtr(1);
      }
      return CallNextHookEx(kbHook, nCode, wParam, lParam);
    }

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
      if (nCode >= 0)
      {
        var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
        if (HandleMouse(wParam.ToInt32(), m.mouseData, m.flags, m.dwExtraInfo.ToUInt64(), m.ptX, m.ptY, false)) return new IntPtr(1);
      }
      return CallNextHookEx(msHook, nCode, wParam, lParam);
    }

    // Returns true to swallow. Real keys after the release are not logged.
    bool HandleKey(uint vk, uint scan, uint flags, int msg, ulong extra, bool simulated)
    {
      bool watching = tracker.Watching;
      var r = tracker.OnKey(vk, flags, msg);
      if (r.Action == HookAction.PassSkipped)
      {
        if (watching)
          RecordSkip("key " + r.SkipReason + " " + InputFilter.KeyMsgName(msg) + " vk=0x" + vk.ToString("X2") + " flags=0x" + flags.ToString("X2") + " extra=0x" + extra.ToString("X"),
            () => "key(" + r.SkipReason + ") " + InputFilter.KeyMsgName(msg) + " vk=0x" + vk.ToString("X2") + " scan=0x" + scan.ToString("X2")
              + " flags=" + InputFilter.KeyFlagsText(flags) + " extra=" + InputFilter.ExtraText(extra));
      }
      else if (r.Action == HookAction.Trigger)
      {
        Enqueue("release by key " + InputFilter.KeyMsgName(msg) + " vk=0x" + vk.ToString("X2") + " scan=0x" + scan.ToString("X2")
          + " flags=" + InputFilter.KeyFlagsText(flags) + " extra=" + InputFilter.ExtraText(extra) + (simulated ? " (simulated)" : "") + " " + RecentRealInputText());
      }
      return r.Swallowed;
    }

    bool HandleMouse(int msg, uint mouseData, uint flags, ulong extra, int x, int y, bool simulated)
    {
      bool watching = tracker.Watching;
      var r = tracker.OnMouse(msg, mouseData, flags);
      if (r.Action == HookAction.PassSkipped)
      {
        if (watching)
          RecordSkip("mouse " + r.SkipReason + " " + InputFilter.MouseMsgName(msg) + " flags=0x" + flags.ToString("X2") + " extra=0x" + extra.ToString("X"),
            () => "mouse(" + r.SkipReason + ") " + InputFilter.MouseMsgName(msg) + " mouseData=0x" + mouseData.ToString("X") + " flags=" + InputFilter.MouseFlagsText(flags)
              + " extra=" + InputFilter.ExtraText(extra) + " pt=" + x + "," + y);
      }
      else if (r.Action == HookAction.Trigger)
      {
        Enqueue("release by mouse " + InputFilter.MouseMsgName(msg) + " mouseData=0x" + mouseData.ToString("X") + " flags=" + InputFilter.MouseFlagsText(flags)
          + " extra=" + InputFilter.ExtraText(extra) + " pt=" + x + "," + y + (simulated ? " (simulated)" : "") + " " + RecentRealInputText());
      }
      else if (msg == InputFilter.WM_MOUSEMOVE && watching)
      {
        lastRealMoveTick = Environment.TickCount;
        anyRealMove = true;
        realMoveCount++;
        recentMoves[recentMovesPos] = lastRealMoveTick;
        recentMovesPos = (recentMovesPos + 1) % recentMoves.Length;
      }
      return r.Swallowed;
    }
  }
}
