using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenOnlySleep
{
  internal sealed class TrayApp : ApplicationContext
  {
    const int HotkeyId = 1;

    readonly AppPaths paths;
    readonly Logger log;
    readonly DdcController ddc;
    readonly ControlWindow ctl;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem blankItem;
    readonly SessionGate gate = new SessionGate();
    readonly Autostart autostart = new Autostart();
    Settings settings;
    BlankSession current;
    bool hotkeyRegistered, exiting;
    SettingsForm settingsForm;

    public TrayApp(AppPaths paths, Logger log, DdcController ddc, double exitAfterSeconds)
    {
      this.paths = paths;
      this.log = log;
      this.ddc = ddc;
      settings = Settings.Load(paths.SettingsPath);

      ctl = new ControlWindow();
      ctl.HotkeyPressed += id => { if (id == HotkeyId) StartBlank("hotkey"); };
      ctl.DisplayChanged += () => current?.OnDisplayChange();
      ctl.EndingSession += OnEndingSession;

      var menu = new ContextMenuStrip();
      blankItem = new ToolStripMenuItem(Strings.MenuBlankNow, null, (s, e) => StartBlank("menu"));
      menu.Items.Add(blankItem);
      menu.Items.Add(new ToolStripMenuItem(Strings.MenuSettings, null, (s, e) => OpenSettings()));
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add(new ToolStripMenuItem(Strings.MenuExit, null, (s, e) => Exit()));

      tray = new NotifyIcon { Icon = TrayIcon.Create(), ContextMenuStrip = menu, Visible = true };

      RegisterCurrentHotkey(true);

      if (exitAfterSeconds > 0)
      {
        var t = new Timer { Interval = (int)Math.Min(int.MaxValue, exitAfterSeconds * 1000) };
        t.Tick += (s, e) => { t.Stop(); log.Log("exit-after elapsed"); Exit(); };
        t.Start();
      }
    }

    // ---------- hotkey ----------
    bool TryRegister(Hotkey h)
    {
      UnregisterHotkey();
      if (NativeMethods.RegisterHotKey(ctl.Handle, HotkeyId, h.NativeModifiers, (uint)h.Key))
      {
        hotkeyRegistered = true;
        log.Log("hotkey registered " + h);
        return true;
      }
      log.Log("hotkey " + h + " could not be registered (error " + Marshal.GetLastWin32Error() + ")");
      return false;
    }

    void UnregisterHotkey()
    {
      if (!hotkeyRegistered) return;
      NativeMethods.UnregisterHotKey(ctl.Handle, HotkeyId);
      hotkeyRegistered = false;
    }

    void RegisterCurrentHotkey(bool notify)
    {
      bool ok = TryRegister(settings.Hotkey);
      UpdateTrayText();
      if (!ok && notify)
      {
        tray.ShowBalloonTip(10000, Strings.HotkeyConflictTitle, Strings.HotkeyConflict(settings.Hotkey.ToString()), ToolTipIcon.Warning);
        log.Log("hotkey conflict shown to the user");
      }
    }

    void UpdateTrayText()
    {
      string text = Strings.TrayTip(settings.Hotkey.ToString()) + (hotkeyRegistered ? "" : Strings.HotkeyNotRegisteredSuffix);
      tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;   // NotifyIcon.Text limit
      blankItem.ShortcutKeyDisplayString = hotkeyRegistered ? settings.Hotkey.ToString() : null;
    }

    // ---------- sessions ----------
    void StartBlank(string source)
    {
      if (exiting) return;
      if (!gate.TryBegin(DateTime.UtcNow))
      {
        log.Log("start request from " + source + " ignored (screens are off, or were just turned back on)");
        return;
      }
      log.Log("start request from " + source);
      current = new BlankSession(new SessionOptions
      {
        Hotkey = settings.Hotkey,
        UseDdc = settings.UseDdc,
        MaxHours = settings.AutoReleaseHours,
      }, log, ddc);
      current.Ended += s =>
      {
        gate.End(DateTime.UtcNow);
        if (current == s) current = null;
      };
      current.Start();
    }

    void OnEndingSession()
    {
      log.Log("Windows is ending the session");
      current?.Abort("endsession");
      ddc.RestoreNow("endsession", TimeSpan.FromSeconds(10));
    }

    // ---------- settings ----------
    void OpenSettings()
    {
      if (settingsForm != null) { settingsForm.Activate(); return; }
      // the hotkey is released while the form is open so it can be pressed in the hotkey box
      UnregisterHotkey();
      bool autostartOn;
      try { autostartOn = autostart.IsEnabled; } catch { autostartOn = false; }
      using (settingsForm = new SettingsForm(settings, autostartOn, TryRegister))
      {
        if (settingsForm.ShowDialog() == DialogResult.OK)
        {
          settings = settingsForm.Result;
          try { settings.Save(paths.SettingsPath); }
          catch (Exception ex) { log.Log("settings save failed: " + ex.Message); }
          if (settingsForm.AutostartChanged)
          {
            try { autostart.Set(settingsForm.Autostart, Application.ExecutablePath); log.Log("start with Windows=" + settingsForm.Autostart); }
            catch (Exception ex) { MessageBox.Show(Strings.AutostartFailed(ex.Message), Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
          }
          log.Log("settings saved hotkey=" + settings.Hotkey + " ddc=" + settings.UseDdc + " hours=" + settings.AutoReleaseHours);
          UpdateTrayText();   // the new hotkey was registered by the form
        }
        else RegisterCurrentHotkey(true);
      }
      settingsForm = null;
    }

    // ---------- exit ----------
    void Exit()
    {
      if (exiting) return;
      exiting = true;
      current?.Abort("exit");
      UnregisterHotkey();
      ddc.RestoreNow("exit", TimeSpan.FromSeconds(15));
      tray.Visible = false;
      tray.Dispose();
      ctl.DestroyHandle();
      ExitThread();
    }
  }

  // A small crescent moon drawn at run time (no icon file in the repository).
  internal static class TrayIcon
  {
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public static Icon Create()
    {
      int size = SystemInformation.SmallIconSize.Width;
      if (size < 16) size = 16;
      using (var bmp = new Bitmap(size, size))
      {
        using (var g = Graphics.FromImage(bmp))
        {
          g.SmoothingMode = SmoothingMode.AntiAlias;
          g.Clear(Color.Transparent);
          float pad = size * 0.08f, d = size - pad * 2;
          using (var moon = new GraphicsPath())
          using (var cut = new GraphicsPath())
          {
            moon.AddEllipse(pad, pad, d, d);
            cut.AddEllipse(pad + d * 0.32f, pad - d * 0.12f, d, d);
            using (var region = new Region(moon))
            {
              region.Exclude(cut);
              using (var b = new SolidBrush(Color.FromArgb(255, 236, 196, 92))) g.FillRegion(b, region);
            }
          }
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
      }
    }
  }
}
