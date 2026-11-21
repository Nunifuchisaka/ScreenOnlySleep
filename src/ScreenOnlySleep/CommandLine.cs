using System;
using System.Globalization;

namespace ScreenOnlySleep
{
  // --data-dir <dir>       use another folder for settings, state and log (default %LOCALAPPDATA%\ScreenOnlySleep)
  // --test-seconds <n>     blank once without the tray, release after n seconds, then exit
  // --simulate key|click   with --test-seconds: feed one non-injected key / left click into the hook path
  // --simulate-after <s>   seconds before --simulate fires (default 3)
  // --exit-after <s>       tray mode: exit by itself after s seconds (for testing)
  // --restore-only         put back saved DDC values and exit
  internal sealed class CommandLine
  {
    public string DataDir;
    public int TestSeconds;
    public string Simulate;
    public double SimulateAfter = 3;
    public double ExitAfter;
    public bool RestoreOnly;
    public string Error;

    public static CommandLine Parse(string[] args)
    {
      var c = new CommandLine();
      for (int i = 0; i < args.Length; i++)
      {
        string a = args[i];
        Func<string> next = () => i + 1 < args.Length ? args[++i] : null;
        switch (a.ToLowerInvariant())
        {
          case "--data-dir":
            c.DataDir = next();
            if (string.IsNullOrEmpty(c.DataDir)) c.Error = "--data-dir needs a folder";
            break;
          case "--test-seconds":
            int n;
            if (!int.TryParse(next(), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) c.Error = "--test-seconds needs a positive integer";
            else c.TestSeconds = n;
            break;
          case "--simulate":
            c.Simulate = (next() ?? "").ToLowerInvariant();
            if (c.Simulate != "key" && c.Simulate != "click") c.Error = "--simulate must be key or click";
            break;
          case "--simulate-after":
            if (!TryPositive(next(), out c.SimulateAfter)) c.Error = "--simulate-after needs a positive number";
            break;
          case "--exit-after":
            if (!TryPositive(next(), out c.ExitAfter)) c.Error = "--exit-after needs a positive number";
            break;
          case "--restore-only":
            c.RestoreOnly = true;
            break;
          default:
            c.Error = "unknown argument: " + a;
            break;
        }
        if (c.Error != null) break;
      }
      if (c.Error == null && c.Simulate != null && c.TestSeconds == 0) c.Error = "--simulate needs --test-seconds";
      return c;
    }

    static bool TryPositive(string s, out double v)
    {
      return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0;
    }
  }
}
