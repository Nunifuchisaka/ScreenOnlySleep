using System;
using System.Threading;
using System.Windows.Forms;

namespace ScreenOnlySleep
{
  internal static class Program
  {
    public const int ExitTestTimer = 0, ExitReleasedByKey = 10, ExitReleasedByButton = 11, ExitOther = 12;
    public const int ExitBadArgs = 2, ExitAlreadyRunning = 3;
    const string MutexName = @"Local\ScreenOnlySleep.SingleInstance";

    [STAThread]
    static int Main(string[] args)
    {
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);

      var cmd = CommandLine.Parse(args);
      if (cmd.Error != null)
      {
        MessageBox.Show(cmd.Error, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        return ExitBadArgs;
      }

      var paths = new AppPaths(cmd.DataDir ?? AppPaths.DefaultDataDir());
      var log = new Logger(paths.LogPath);

      using (var mutex = new Mutex(false, MutexName))
      {
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
          if (cmd.TestSeconds == 0 && !cmd.RestoreOnly) MessageBox.Show(Strings.AlreadyRunning, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
          return ExitAlreadyRunning;
        }
        try { return Run(cmd, paths, log); }
        finally { mutex.ReleaseMutex(); }
      }
    }

    static int Run(CommandLine cmd, AppPaths paths, Logger log)
    {
      log.Log("start version=" + Application.ProductVersion + " args=" + string.Join(" ", Environment.GetCommandLineArgs(), 1, Environment.GetCommandLineArgs().Length - 1));
      using (var ddc = new DdcController(paths.StatePath, log))
      {
        Application.ThreadException += (s, e) => { log.Log("unhandled: " + e.Exception); ddc.RestoreNow("crash", TimeSpan.FromSeconds(10)); };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { log.Log("unhandled: " + e.ExceptionObject); ddc.RestoreNow("crash", TimeSpan.FromSeconds(10)); };

        // A previous run that was killed may have left monitors dark: put them back first, even if DDC is now turned off.
        if (ddc.HasPendingState && !ddc.RestoreNow("leftover", TimeSpan.FromSeconds(30))) ddc.EnqueueRestore("leftover", true);
        if (cmd.RestoreOnly)
        {
          log.Log("restore-only done");
          return ddc.HasPendingState ? ExitOther : 0;
        }

        int code;
        try
        {
          if (cmd.TestSeconds > 0)
          {
            var ctx = new TestRunContext(cmd, paths, log, ddc);
            Application.Run(ctx);
            code = ctx.ExitCode;
          }
          else
          {
            Application.Run(new TrayApp(paths, log, ddc, cmd.ExitAfter));
            code = 0;
          }
        }
        finally
        {
          if (ddc.HasPendingState) ddc.RestoreNow("exit", TimeSpan.FromSeconds(15));
          log.Log("exit");
        }
        return code;
      }
    }
  }

  // --test-seconds: one blank session without the tray or a hotkey, then exit after DDC has been restored.
  internal sealed class TestRunContext : ApplicationContext
  {
    readonly DdcController ddc;
    readonly ControlWindow ctl;
    readonly BlankSession session;
    public int ExitCode = Program.ExitOther;

    public TestRunContext(CommandLine cmd, AppPaths paths, Logger log, DdcController ddc)
    {
      this.ddc = ddc;
      var settings = Settings.Load(paths.SettingsPath);
      session = new BlankSession(new SessionOptions
      {
        Hotkey = settings.Hotkey,
        UseDdc = settings.UseDdc,
        MaxHours = settings.AutoReleaseHours,
        TestSeconds = cmd.TestSeconds,
      }, log, ddc);
      ctl = new ControlWindow();
      ctl.DisplayChanged += () => session.OnDisplayChange();
      ctl.EndingSession += () => { session.Abort("endsession"); ddc.RestoreNow("endsession", TimeSpan.FromSeconds(10)); };
      session.Ended += OnEnded;
      session.Start();

      if (cmd.Simulate != null)
      {
        var t = new System.Windows.Forms.Timer { Interval = (int)(cmd.SimulateAfter * 1000) };
        t.Tick += (s, e) =>
        {
          t.Stop();
          t.Dispose();
          log.Log("simulating a real " + cmd.Simulate);
          if (cmd.Simulate == "key") session.SimulateRealKey(0x41 /* A */);
          else session.SimulateRealClick();
        };
        t.Start();
      }
    }

    void OnEnded(BlankSession s)
    {
      ExitCode = s.Triggered == TriggerKind.Input
        ? (s.Reason == "key" ? Program.ExitReleasedByKey : Program.ExitReleasedByButton)
        : (s.Reason == "test" ? Program.ExitTestTimer : Program.ExitOther);
      ddc.WaitIdle(TimeSpan.FromSeconds(70));
      ctl.DestroyHandle();
      ExitThread();
    }
  }
}
