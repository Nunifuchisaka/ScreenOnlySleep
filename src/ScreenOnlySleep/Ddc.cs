using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using static ScreenOnlySleep.NativeMethods;

namespace ScreenOnlySleep
{
  internal sealed class PhysMon { public string Key; public string Desc; public IntPtr Handle; }

  internal sealed class MonInfo
  {
    public string Device;
    public RECT Rect;
    public List<PhysMon> Phys = new List<PhysMon>();
    public PHYSICAL_MONITOR[] Raw;
  }

  internal static class Monitors
  {
    public static List<MonInfo> Enumerate(bool physical)
    {
      var list = new List<MonInfo>();
      MonitorEnumProc proc = delegate (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
      {
        var mi = new MONITORINFOEX();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
        GetMonitorInfo(hMon, ref mi);
        var m = new MonInfo { Device = mi.szDevice, Rect = mi.rcMonitor };
        if (physical)
        {
          uint n;
          if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out n) && n > 0)
          {
            var arr = new PHYSICAL_MONITOR[n];
            if (GetPhysicalMonitorsFromHMONITOR(hMon, n, arr))
            {
              m.Raw = arr;
              for (int i = 0; i < arr.Length; i++)
                m.Phys.Add(new PhysMon { Key = m.Device + "#" + i, Desc = arr[i].szPhysicalMonitorDescription, Handle = arr[i].hPhysicalMonitor });
            }
          }
        }
        list.Add(m);
        return true;
      };
      EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
      GC.KeepAlive(proc);
      return list;
    }

    public static void Free(List<MonInfo> list)
    {
      foreach (var m in list) if (m.Raw != null) DestroyPhysicalMonitors((uint)m.Raw.Length, m.Raw);
    }
  }

  // Lowers brightness (VCP 0x10) to 0 over DDC/CI and puts it back. Monitors that do not answer are left alone
  // (they are only covered by the black window). All DDC traffic runs on one worker thread, in order.
  internal sealed class DdcController : IDisposable
  {
    public const byte VCP_BRIGHTNESS = 0x10;
    static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(60);

    readonly string statePath;
    readonly Logger log;
    readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
    readonly Thread worker;
    int generation;   // bumped by Apply so an older restore retry loop stops

    public DdcController(string statePath, Logger log)
    {
      this.statePath = statePath;
      this.log = log;
      worker = new Thread(() => { foreach (var a in queue.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { log.Log("ddc error: " + ex); } } });
      worker.IsBackground = true;
      worker.Name = "DDC";
      worker.Start();
    }

    public bool HasPendingState { get { return DdcStateFile.Read(statePath).Count > 0; } }

    public void EnqueueApply()
    {
      Interlocked.Increment(ref generation);
      queue.Add(Apply);
    }

    // Restores the saved values; if some fail, keeps retrying every second for up to a minute
    // (monitors waking up may not answer DDC at once). Values that still fail stay in the state file for the next start.
    public void EnqueueRestore(string why, bool retry)
    {
      int gen = Volatile.Read(ref generation);
      queue.Add(() =>
      {
        if (Restore(why) || !retry) return;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < RetryWindow && Volatile.Read(ref generation) == gen)
        {
          Thread.Sleep(1000);
          if (Volatile.Read(ref generation) != gen) return;   // a new session took over the saved values
          if (Restore("retry")) return;
        }
        log.Log("restore gave up; values kept in the state file for the next start");
      });
    }

    // Restores once without retrying and waits for it (exit, Windows shutdown). Stops a running retry loop first.
    public bool RestoreNow(string why, TimeSpan timeout)
    {
      Interlocked.Increment(ref generation);
      bool ok = false;
      var done = new ManualResetEventSlim(false);
      try { queue.Add(() => { ok = Restore(why); done.Set(); }); }
      catch (InvalidOperationException) { return false; }
      return done.Wait(timeout) && ok;
    }

    // Waits until every queued job has finished. Returns false on timeout.
    public bool WaitIdle(TimeSpan timeout)
    {
      var done = new ManualResetEventSlim(false);
      try { queue.Add(() => done.Set()); }
      catch (InvalidOperationException) { return true; }
      return done.Wait(timeout);
    }

    static bool TryGet(IntPtr h, byte code, out uint cur, out uint max)
    {
      uint type;
      for (int i = 0; i < 3; i++)
      {
        if (GetVCPFeatureAndVCPFeatureReply(h, code, out type, out cur, out max)) return true;
        Thread.Sleep(60);
      }
      cur = 0; max = 0;
      return false;
    }

    // Writes, then reads back to confirm.
    static bool SetVerify(IntPtr h, byte code, uint value)
    {
      for (int i = 0; i < 3; i++)
      {
        SetVCPFeature(h, code, value);
        Thread.Sleep(150);
        uint cur, max;
        if (TryGet(h, code, out cur, out max) && cur == value) return true;
        Thread.Sleep(100);
      }
      return false;
    }

    static PhysMon Find(List<MonInfo> mons, string key)
    {
      foreach (var m in mons) foreach (var p in m.Phys) if (p.Key == key) return p;
      return null;
    }

    // Values already in the state file are the originals (left over from an unfinished session) and win over a fresh read.
    void Apply()
    {
      var state = DdcStateFile.Read(statePath);
      var mons = Monitors.Enumerate(true);
      var toApply = new List<PhysMon>();
      try
      {
        foreach (var m in mons)
        {
          foreach (var p in m.Phys)
          {
            DdcEntry e = null;
            foreach (var s in state) if (s.Key == p.Key && s.Code == VCP_BRIGHTNESS) e = s;
            if (e == null)
            {
              uint cur, max;
              if (TryGet(p.Handle, VCP_BRIGHTNESS, out cur, out max))
              {
                e = new DdcEntry { Key = p.Key, Code = VCP_BRIGHTNESS, Value = cur };
                state.Add(e);
              }
            }
            if (e == null) log.Log("plan " + p.Key + " '" + p.Desc + "' no DDC brightness -> black window only");
            else
            {
              log.Log("plan " + p.Key + " '" + p.Desc + "' brightness original=" + e.Value);
              toApply.Add(p);
            }
          }
        }
        DdcStateFile.Write(statePath, state);   // save the originals before changing anything
        foreach (var p in toApply)
        {
          bool ok = SetVerify(p.Handle, VCP_BRIGHTNESS, 0);
          log.Log("apply " + p.Key + " brightness=0 " + (ok ? "ok" : "NG"));
        }
      }
      finally { Monitors.Free(mons); }
    }

    // Puts saved values back. Values that could not be restored stay in the state file.
    bool Restore(string why)
    {
      var state = DdcStateFile.Read(statePath);
      if (state.Count == 0) return true;
      var mons = Monitors.Enumerate(true);
      var failed = new List<DdcEntry>();
      try
      {
        foreach (var e in state)
        {
          var pm = Find(mons, e.Key);
          bool ok = pm != null && SetVerify(pm.Handle, e.Code, e.Value);
          log.Log("restore(" + why + ") " + e.Key + " 0x" + e.Code.ToString("X2") + "=" + e.Value + (ok ? " ok" : pm == null ? " NG(not found)" : " NG"));
          if (!ok) failed.Add(e);
        }
      }
      finally { Monitors.Free(mons); }
      DdcStateFile.Write(statePath, failed);
      return failed.Count == 0;
    }

    public void Dispose()
    {
      queue.CompleteAdding();
      worker.Join(TimeSpan.FromSeconds(15));
    }
  }
}
