using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ScreenOnlySleep
{
  // settings.ini: one "Key=Value" per line. Unknown keys and invalid values fall back to the defaults.
  internal sealed class Settings
  {
    public const double MinAutoReleaseHours = 0.25;
    public const double MaxAutoReleaseHours = 48;
    public const double DefaultAutoReleaseHours = 12;

    public Hotkey Hotkey = Hotkey.Default;
    public bool UseDdc = true;
    public double AutoReleaseHours = DefaultAutoReleaseHours;

    public Settings Clone()
    {
      return new Settings { Hotkey = Hotkey, UseDdc = UseDdc, AutoReleaseHours = AutoReleaseHours };
    }

    public static double ClampHours(double h)
    {
      if (double.IsNaN(h) || double.IsInfinity(h)) return DefaultAutoReleaseHours;
      if (h < MinAutoReleaseHours) return MinAutoReleaseHours;
      if (h > MaxAutoReleaseHours) return MaxAutoReleaseHours;
      return h;
    }

    public static Settings Parse(string text)
    {
      var s = new Settings();
      if (text == null) return s;
      foreach (var raw in text.Split('\n'))
      {
        string line = raw.Trim();
        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
        int eq = line.IndexOf('=');
        if (eq <= 0) continue;
        string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
        switch (key.ToLowerInvariant())
        {
          case "hotkey":
            Hotkey h;
            if (Hotkey.TryParse(value, out h)) s.Hotkey = h;
            break;
          case "useddc":
            bool b;
            if (bool.TryParse(value, out b)) s.UseDdc = b;
            break;
          case "autoreleasehours":
            double d;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) s.AutoReleaseHours = ClampHours(d);
            break;
        }
      }
      return s;
    }

    public string Serialize()
    {
      var sb = new StringBuilder();
      sb.Append("# ScreenOnlySleep settings\r\n");
      sb.Append("Hotkey=" + Hotkey + "\r\n");
      sb.Append("UseDdc=" + (UseDdc ? "true" : "false") + "\r\n");
      sb.Append("AutoReleaseHours=" + AutoReleaseHours.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n");
      return sb.ToString();
    }

    public static Settings Load(string path)
    {
      try { return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : new Settings(); }
      catch (IOException) { return new Settings(); }
      catch (UnauthorizedAccessException) { return new Settings(); }
    }

    public void Save(string path)
    {
      FileUtil.WriteAllTextAtomic(path, Serialize(), new UTF8Encoding(false));
    }
  }

  // Start with Windows through a value under HKCU\...\Run. The key is injectable so tests never touch the real one.
  internal sealed class Autostart
  {
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ScreenOnlySleep";

    readonly RegistryKey root;
    readonly string subKey, valueName;

    public Autostart() : this(Registry.CurrentUser, RunKeyPath, ValueName) { }

    public Autostart(RegistryKey root, string subKey, string valueName)
    {
      this.root = root;
      this.subKey = subKey;
      this.valueName = valueName;
    }

    public static string CommandFor(string exePath) { return "\"" + exePath + "\""; }

    public string GetCommand()
    {
      using (var k = root.OpenSubKey(subKey, false))
        return k == null ? null : k.GetValue(valueName) as string;
    }

    public bool IsEnabled { get { return GetCommand() != null; } }

    public void Set(bool enabled, string exePath)
    {
      if (enabled)
      {
        using (var k = root.CreateSubKey(subKey, true))
          k.SetValue(valueName, CommandFor(exePath), RegistryValueKind.String);
      }
      else
      {
        using (var k = root.OpenSubKey(subKey, true))
          if (k != null && k.GetValue(valueName) != null) k.DeleteValue(valueName, false);
      }
    }
  }

  internal static class FileUtil
  {
    public static void WriteAllTextAtomic(string path, string text, Encoding enc)
    {
      string dir = Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
      string tmp = path + ".tmp";
      File.WriteAllText(tmp, text, enc);
      if (File.Exists(path)) File.Replace(tmp, path, null);
      else File.Move(tmp, path);
    }
  }

  // Original brightness values, saved before they are changed so a crash can be undone on the next start.
  // One "monitorKey<TAB>vcpCodeHex<TAB>value" per line.
  internal sealed class DdcEntry
  {
    public string Key;
    public byte Code;
    public uint Value;
  }

  internal static class DdcStateFile
  {
    public static List<DdcEntry> Read(string path)
    {
      var list = new List<DdcEntry>();
      if (!File.Exists(path)) return list;
      foreach (var line in File.ReadAllLines(path))
      {
        var f = line.Split('\t');
        if (f.Length != 3 || f[0].Length == 0) continue;
        byte code; uint value;
        if (!byte.TryParse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) continue;
        if (!uint.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out value)) continue;
        list.Add(new DdcEntry { Key = f[0], Code = code, Value = value });
      }
      return list;
    }

    // An empty list deletes the file.
    public static void Write(string path, List<DdcEntry> list)
    {
      if (list.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
      var sb = new StringBuilder();
      foreach (var e in list) sb.Append(e.Key + "\t" + e.Code.ToString("X2") + "\t" + e.Value.ToString(CultureInfo.InvariantCulture) + "\r\n");
      FileUtil.WriteAllTextAtomic(path, sb.ToString(), Encoding.ASCII);
    }
  }
}
