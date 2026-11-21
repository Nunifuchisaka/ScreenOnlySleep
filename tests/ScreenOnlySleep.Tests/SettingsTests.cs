using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;
using Xunit;

namespace ScreenOnlySleep.Tests
{
  public sealed class TempDir : IDisposable
  {
    public string Path { get; private set; }
    public TempDir()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ScreenOnlySleep.Tests." + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Path);
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }

  public class HotkeyTests
  {
    [Theory]
    [InlineData("Ctrl+Alt+F12", "Ctrl+Alt+F12")]
    [InlineData("alt+ctrl+f12", "Ctrl+Alt+F12")]
    [InlineData(" Ctrl + Shift + B ", "Ctrl+Shift+B")]
    [InlineData("Win+Alt+Pause", "Alt+Win+Pause")]
    [InlineData("Control+1", "Ctrl+D1")]
    [InlineData("Ctrl+D1", "Ctrl+D1")]
    public void ParsesAndFormats(string text, string expected)
    {
      Hotkey h;
      Assert.True(Hotkey.TryParse(text, out h));
      Assert.Equal(expected, h.ToString());
      Hotkey again;
      Assert.True(Hotkey.TryParse(h.ToString(), out again));
      Assert.Equal(h, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("F12")]            // no modifier
    [InlineData("Ctrl+Alt")]       // no key
    [InlineData("Ctrl+A+B")]       // two keys
    [InlineData("Ctrl+F15")]       // ignored by the release filter
    [InlineData("Ctrl+NoSuchKey")]
    [InlineData("Ctrl+123")]
    [InlineData("Ctrl++")]
    [InlineData("Ctrl+ShiftKey")]
    public void RejectsInvalid(string text)
    {
      Hotkey h;
      Assert.False(Hotkey.TryParse(text, out h));
      Assert.Null(h);
    }

    [Fact]
    public void DefaultIsCtrlAltF12WithNoRepeat()
    {
      Assert.Equal("Ctrl+Alt+F12", Hotkey.Default.ToString());
      Assert.Equal(0x4000u | 0x2u | 0x1u, Hotkey.Default.NativeModifiers);
      Assert.Equal(Keys.F12, Hotkey.Default.Key);
    }

    [Fact]
    public void ReleaseWaitKeysFollowTheHotkey()
    {
      Assert.Equal(new[] { 0x11, 0x12, 0x7B }, Hotkey.Default.ReleaseWaitKeys().ToArray());
      Hotkey h;
      Hotkey.TryParse("Shift+Win+K", out h);
      Assert.Equal(new[] { 0x10, 0x5B, 0x5C, (int)Keys.K }, h.ReleaseWaitKeys().ToArray());
    }

    [Fact]
    public void ExtraWaitKeysCoverMenuStarts()
    {
      Assert.Contains(Hotkey.VK_LBUTTON, Hotkey.ExtraReleaseWaitKeys);
      Assert.Contains(Hotkey.VK_RBUTTON, Hotkey.ExtraReleaseWaitKeys);
      Assert.Contains(Hotkey.VK_RETURN, Hotkey.ExtraReleaseWaitKeys);
      Assert.Contains(Hotkey.VK_SPACE, Hotkey.ExtraReleaseWaitKeys);
    }
  }

  public class ArmGateTests
  {
    [Fact]
    public void ArmsOnlyWhenEveryKeyIsUpAndStaysArmed()
    {
      var down = new HashSet<int> { 0x11, 0x7B };
      var g = new ArmGate(new[] { 0x11, 0x12, 0x7B }, vk => down.Contains(vk));
      Assert.False(g.Poll());
      down.Remove(0x11);
      Assert.False(g.Poll());   // F12 still held (key repeat)
      Assert.False(g.Armed);
      down.Remove(0x7B);
      Assert.True(g.Poll());
      Assert.True(g.Armed);
      down.Add(0x11);           // pressing again later does not disarm
      Assert.False(g.Poll());
      Assert.True(g.Armed);
    }
  }

  public class SessionGateTests
  {
    [Fact]
    public void RefusesWhileActiveAndDuringCooldown()
    {
      var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
      var g = new SessionGate(TimeSpan.FromSeconds(1.5));
      Assert.True(g.TryBegin(t0));
      Assert.True(g.Active);
      Assert.False(g.TryBegin(t0.AddSeconds(1)));     // hotkey pressed again while the screens are off
      g.End(t0.AddSeconds(10));
      Assert.False(g.Active);
      Assert.False(g.TryBegin(t0.AddSeconds(11)));    // the press that released the screens
      Assert.True(g.TryBegin(t0.AddSeconds(11.6)));
    }
  }

  public class SettingsTests
  {
    [Fact]
    public void DefaultsWhenEmptyOrMissing()
    {
      var s = Settings.Parse("");
      Assert.Equal(Hotkey.Default, s.Hotkey);
      Assert.True(s.UseDdc);
      Assert.Equal(12, s.AutoReleaseHours);
      using (var d = new TempDir())
      {
        var loaded = Settings.Load(Path.Combine(d.Path, "nope.ini"));
        Assert.Equal(Hotkey.Default, loaded.Hotkey);
      }
    }

    [Fact]
    public void ParsesValues()
    {
      var s = Settings.Parse("# comment\r\n; other\r\nHotkey = Ctrl+Shift+F11\r\nuseddc=False\r\nAutoReleaseHours=1.5\r\nUnknown=1\r\n");
      Assert.Equal("Ctrl+Shift+F11", s.Hotkey.ToString());
      Assert.False(s.UseDdc);
      Assert.Equal(1.5, s.AutoReleaseHours);
    }

    [Fact]
    public void InvalidValuesFallBack()
    {
      var s = Settings.Parse("Hotkey=F12\nUseDdc=maybe\nAutoReleaseHours=abc\n");
      Assert.Equal(Hotkey.Default, s.Hotkey);
      Assert.True(s.UseDdc);
      Assert.Equal(12, s.AutoReleaseHours);
    }

    [Theory]
    [InlineData("0", 0.25)]
    [InlineData("-3", 0.25)]
    [InlineData("1000", 48)]
    [InlineData("24", 24)]
    public void HoursAreClamped(string value, double expected)
    {
      Assert.Equal(expected, Settings.Parse("AutoReleaseHours=" + value).AutoReleaseHours);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
      using (var d = new TempDir())
      {
        string path = Path.Combine(d.Path, "sub", "settings.ini");
        Hotkey h;
        Hotkey.TryParse("Ctrl+Alt+Shift+F10", out h);
        var s = new Settings { Hotkey = h, UseDdc = false, AutoReleaseHours = 0.75 };
        s.Save(path);
        s.Save(path);   // overwrite path
        var loaded = Settings.Load(path);
        Assert.Equal(h, loaded.Hotkey);
        Assert.False(loaded.UseDdc);
        Assert.Equal(0.75, loaded.AutoReleaseHours);
        Assert.False(File.Exists(path + ".tmp"));
      }
    }

    [Fact]
    public void HoursUseInvariantCulture()
    {
      var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
      try
      {
        System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        var s = new Settings { AutoReleaseHours = 2.5 };
        Assert.Contains("AutoReleaseHours=2.5", s.Serialize());
        Assert.Equal(2.5, Settings.Parse(s.Serialize()).AutoReleaseHours);
      }
      finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
    }
  }

  public class DdcStateFileTests
  {
    [Fact]
    public void RoundTripAndEmptyDeletes()
    {
      using (var d = new TempDir())
      {
        string path = Path.Combine(d.Path, "ddc-state.tsv");
        Assert.Empty(DdcStateFile.Read(path));
        DdcStateFile.Write(path, new List<DdcEntry>
        {
          new DdcEntry { Key = @"\\.\DISPLAY1#0", Code = 0x10, Value = 70 },
          new DdcEntry { Key = @"\\.\DISPLAY2#0", Code = 0x10, Value = 0 },
        });
        var back = DdcStateFile.Read(path);
        Assert.Equal(2, back.Count);
        Assert.Equal(@"\\.\DISPLAY1#0", back[0].Key);
        Assert.Equal(0x10, back[0].Code);
        Assert.Equal(70u, back[0].Value);
        Assert.Equal(0u, back[1].Value);
        DdcStateFile.Write(path, new List<DdcEntry>());
        Assert.False(File.Exists(path));
      }
    }

    [Fact]
    public void MalformedLinesAreSkipped()
    {
      using (var d = new TempDir())
      {
        string path = Path.Combine(d.Path, "ddc-state.tsv");
        File.WriteAllText(path, "a\t10\t50\r\nbroken\r\nb\tZZ\t1\r\nc\t10\t-1\r\n\t10\t5\r\nd\t10\t30\textra\r\n");
        var back = DdcStateFile.Read(path);
        Assert.Single(back);
        Assert.Equal("a", back[0].Key);
      }
    }
  }

  public class AutostartTests
  {
    [Fact]
    public void SetAndClearUnderATestKey()
    {
      string sub = @"Software\ScreenOnlySleep.Tests\" + Guid.NewGuid().ToString("N");
      try
      {
        var a = new Autostart(Registry.CurrentUser, sub, "ScreenOnlySleep");
        Assert.False(a.IsEnabled);
        a.Set(false, @"X:\app\ScreenOnlySleep.exe");   // clearing a missing value is fine
        a.Set(true, @"X:\app dir\ScreenOnlySleep.exe");
        Assert.True(a.IsEnabled);
        Assert.Equal("\"X:\\app dir\\ScreenOnlySleep.exe\"", a.GetCommand());
        a.Set(false, @"X:\app dir\ScreenOnlySleep.exe");
        Assert.False(a.IsEnabled);
      }
      finally
      {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\ScreenOnlySleep.Tests", false);
      }
    }

    [Fact]
    public void DefaultTargetsTheRunKey()
    {
      Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", Autostart.RunKeyPath);
      Assert.Equal("ScreenOnlySleep", Autostart.ValueName);
    }
  }

  public class CommandLineTests
  {
    [Fact]
    public void ParsesTestOptions()
    {
      var c = CommandLine.Parse(new[] { "--data-dir", @"X:\d", "--test-seconds", "10", "--simulate", "Key", "--simulate-after", "2.5" });
      Assert.Null(c.Error);
      Assert.Equal(@"X:\d", c.DataDir);
      Assert.Equal(10, c.TestSeconds);
      Assert.Equal("key", c.Simulate);
      Assert.Equal(2.5, c.SimulateAfter);
    }

    [Theory]
    [InlineData("--test-seconds")]
    [InlineData("--test-seconds", "0")]
    [InlineData("--test-seconds", "x")]
    [InlineData("--simulate", "key")]
    [InlineData("--test-seconds", "5", "--simulate", "wheel")]
    [InlineData("--data-dir")]
    [InlineData("--bogus")]
    public void RejectsBadArguments(params string[] args)
    {
      Assert.NotNull(CommandLine.Parse(args).Error);
    }

    [Fact]
    public void EmptyIsTrayMode()
    {
      var c = CommandLine.Parse(new string[0]);
      Assert.Null(c.Error);
      Assert.Equal(0, c.TestSeconds);
      Assert.False(c.RestoreOnly);
    }
  }
}
