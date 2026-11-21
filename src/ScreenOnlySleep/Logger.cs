using System;
using System.IO;
using System.Text;

namespace ScreenOnlySleep
{
  internal sealed class AppPaths
  {
    public string DataDir { get; private set; }
    public string SettingsPath { get { return Path.Combine(DataDir, "settings.ini"); } }
    public string StatePath { get { return Path.Combine(DataDir, "ddc-state.tsv"); } }
    public string LogPath { get { return Path.Combine(DataDir, "ScreenOnlySleep.log"); } }

    public AppPaths(string dataDir)
    {
      DataDir = dataDir;
      Directory.CreateDirectory(DataDir);
    }

    public static string DefaultDataDir()
    {
      return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenOnlySleep");
    }
  }

  // Thread-safe append-only log. Rotates to a single ".old" file at about 1 MB.
  internal sealed class Logger
  {
    const long MaxBytes = 1024 * 1024;
    readonly string path;
    readonly object gate = new object();

    public Logger(string path) { this.path = path; }

    public void Log(string text)
    {
      lock (gate)
      {
        try
        {
          var fi = new FileInfo(path);
          if (fi.Exists && fi.Length > MaxBytes)
          {
            string old = Path.ChangeExtension(path, ".old.log");
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
          }
          File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text + "\r\n", new UTF8Encoding(false));
        }
        catch { }
      }
    }
  }
}
