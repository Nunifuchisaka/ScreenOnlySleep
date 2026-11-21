using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ScreenOnlySleep
{
  [Flags]
  internal enum HotkeyModifiers
  {
    None = 0,
    Alt = 0x1,     // MOD_ALT
    Ctrl = 0x2,    // MOD_CONTROL
    Shift = 0x4,   // MOD_SHIFT
    Win = 0x8,     // MOD_WIN
  }

  // A global hotkey such as "Ctrl+Alt+F12".
  internal sealed class Hotkey : IEquatable<Hotkey>
  {
    public const uint MOD_NOREPEAT = 0x4000;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04, VK_XBUTTON1 = 0x05, VK_XBUTTON2 = 0x06, VK_RETURN = 0x0D, VK_SPACE = 0x20;

    public static readonly Hotkey Default = new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, Keys.F12);

    public HotkeyModifiers Modifiers { get; private set; }
    public Keys Key { get; private set; }

    public Hotkey(HotkeyModifiers modifiers, Keys key)
    {
      Modifiers = modifiers;
      Key = key & Keys.KeyCode;
    }

    public uint NativeModifiers { get { return (uint)Modifiers | MOD_NOREPEAT; } }

    static bool IsModifierKey(Keys k)
    {
      switch (k)
      {
        case Keys.ControlKey: case Keys.LControlKey: case Keys.RControlKey:
        case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey:
        case Keys.Menu: case Keys.LMenu: case Keys.RMenu:
        case Keys.LWin: case Keys.RWin:
          return true;
      }
      return false;
    }

    // A usable hotkey needs at least one modifier and a non-modifier key that can end a session
    // (keys the input filter ignores, such as F13-F24, are rejected).
    public bool IsValid
    {
      get
      {
        if (Modifiers == HotkeyModifiers.None) return false;
        if (Key == Keys.None || IsModifierKey(Key)) return false;
        if (InputFilter.KeySkipReason((uint)Key, 0) != null) return false;
        return true;
      }
    }

    // Virtual keys that must all be up before the session starts watching for a release.
    public IList<int> ReleaseWaitKeys()
    {
      var list = new List<int>();
      if ((Modifiers & HotkeyModifiers.Ctrl) != 0) list.Add(VK_CONTROL);
      if ((Modifiers & HotkeyModifiers.Alt) != 0) list.Add(VK_MENU);
      if ((Modifiers & HotkeyModifiers.Shift) != 0) list.Add(VK_SHIFT);
      if ((Modifiers & HotkeyModifiers.Win) != 0) { list.Add(VK_LWIN); list.Add(VK_RWIN); }
      list.Add((int)Key);
      return list;
    }

    // Keys and buttons to wait for in addition to the hotkey, so that a session started from the tray menu
    // (mouse click, or Enter / Space on the menu) is not ended by that same press.
    public static readonly int[] ExtraReleaseWaitKeys = { VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2, VK_RETURN, VK_SPACE };

    public override string ToString()
    {
      var parts = new List<string>();
      if ((Modifiers & HotkeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
      if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
      if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
      if ((Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
      parts.Add(Key.ToString());
      return string.Join("+", parts);
    }

    public static bool TryParse(string text, out Hotkey hotkey)
    {
      hotkey = null;
      if (string.IsNullOrWhiteSpace(text)) return false;
      var mods = HotkeyModifiers.None;
      Keys key = Keys.None;
      foreach (var raw in text.Split('+'))
      {
        string p = raw.Trim();
        if (p.Length == 0) return false;
        switch (p.ToLowerInvariant())
        {
          case "ctrl": case "control": mods |= HotkeyModifiers.Ctrl; continue;
          case "alt": mods |= HotkeyModifiers.Alt; continue;
          case "shift": mods |= HotkeyModifiers.Shift; continue;
          case "win": case "windows": mods |= HotkeyModifiers.Win; continue;
        }
        if (key != Keys.None) return false;   // two non-modifier keys
        Keys k;
        if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;   // "1" -> Keys.D1
        if (!Enum.TryParse(p, true, out k)) return false;
        int n;
        if (int.TryParse(p, out n)) return false;   // reject raw numbers that Enum.TryParse would accept
        key = k & Keys.KeyCode;
        if (key == Keys.None) return false;
      }
      var h = new Hotkey(mods, key);
      if (!h.IsValid) return false;
      hotkey = h;
      return true;
    }

    public bool Equals(Hotkey other) { return other != null && other.Modifiers == Modifiers && other.Key == Key; }
    public override bool Equals(object obj) { return Equals(obj as Hotkey); }
    public override int GetHashCode() { return ((int)Modifiers << 16) ^ (int)Key; }
  }

  // Waits until every listed key is up, then stays armed.
  internal sealed class ArmGate
  {
    readonly IList<int> keys;
    readonly Func<int, bool> isDown;
    bool armed;

    public ArmGate(IList<int> keys, Func<int, bool> isDown)
    {
      this.keys = keys;
      this.isDown = isDown;
    }

    public bool Armed { get { return armed; } }

    // Returns true on the call that arms the gate.
    public bool Poll()
    {
      if (armed) return false;
      foreach (var k in keys) if (isDown(k)) return false;
      armed = true;
      return true;
    }
  }

  // Refuses to start a session while one is running or finishing, and for a short time after one ends,
  // so the key press that released the screens cannot blank them again.
  internal sealed class SessionGate
  {
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(1.5);
    readonly TimeSpan cooldown;
    bool active;
    DateTime cooldownUntil = DateTime.MinValue;

    public SessionGate() : this(DefaultCooldown) { }
    public SessionGate(TimeSpan cooldown) { this.cooldown = cooldown; }

    public bool Active { get { return active; } }

    public bool TryBegin(DateTime now)
    {
      if (active || now < cooldownUntil) return false;
      active = true;
      return true;
    }

    public void End(DateTime now)
    {
      active = false;
      cooldownUntil = now + cooldown;
    }
  }
}
