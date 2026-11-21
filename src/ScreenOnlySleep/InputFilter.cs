using System.Text;

namespace ScreenOnlySleep
{
  // Decides which low-level input events may end a blank session.
  // Keep the skip list minimal: every entry added here is a key that can no longer wake the screens.
  internal static class InputFilter
  {
    public const uint LLKHF_EXTENDED = 0x01, LLKHF_LOWER_IL_INJECTED = 0x02, LLKHF_INJECTED = 0x10, LLKHF_ALTDOWN = 0x20, LLKHF_UP = 0x80;
    public const uint LLMHF_INJECTED = 0x01, LLMHF_LOWER_IL_INJECTED = 0x02;

    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
      WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_MOUSEHWHEEL = 0x020E;

    // Returns why a keyboard event must not end the session, or null if it may.
    public static string KeySkipReason(uint vk, uint flags)
    {
      // Synthesized input (SendInput / keybd_event): automation tools, key remappers, keep-awake tools.
      // LOWER_IL_INJECTED means it came from a process with a lower integrity level.
      if ((flags & LLKHF_LOWER_IL_INJECTED) != 0) return "injected(lower-il)";
      if ((flags & LLKHF_INJECTED) != 0) return "injected";
      // F13-F24 (0x7C-0x87): not on ordinary keyboards; keep-awake tools and macros send them.
      if (vk >= 0x7C && vk <= 0x87) return "F" + (vk - 0x7C + 13);
      // VK_NONAME (0xFC): reserved, never produced by a keyboard.
      if (vk == 0xFC) return "VK_NONAME";
      // 0xFF: scan code without a virtual key (Fn keys, some devices and drivers).
      if (vk == 0xFF) return "vk0xFF";
      // 0: no virtual key at all.
      if (vk == 0) return "vk0";
      return null;
    }

    // Returns why a mouse event must not end the session, or null if it may.
    public static string MouseSkipReason(uint flags)
    {
      if ((flags & LLMHF_LOWER_IL_INJECTED) != 0) return "injected(lower-il)";
      if ((flags & LLMHF_INJECTED) != 0) return "injected";
      return null;
    }

    public static bool IsKeyDown(int msg) { return msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN; }
    public static bool IsKeyUp(int msg) { return msg == WM_KEYUP || msg == WM_SYSKEYUP; }

    // Button id: 1=left, 2=right, 3=middle, 4/5=X1/X2. 0 for move, wheel and anything else.
    public static int ButtonId(int msg, uint mouseData, out bool down, out bool up)
    {
      down = msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN;
      up = msg == WM_LBUTTONUP || msg == WM_RBUTTONUP || msg == WM_MBUTTONUP || msg == WM_XBUTTONUP;
      if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP) return 1;
      if (msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP) return 2;
      if (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP) return 3;
      if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP) return 3 + (int)((mouseData >> 16) & 0xFFFF);
      down = up = false;
      return 0;
    }

    // ---------- text for the log ----------
    public static string KeyFlagsText(uint f)
    {
      var sb = new StringBuilder("0x" + f.ToString("X2"));
      if ((f & LLKHF_INJECTED) != 0) sb.Append(" INJECTED");
      if ((f & LLKHF_LOWER_IL_INJECTED) != 0) sb.Append(" LOWER_IL_INJECTED");
      if ((f & LLKHF_EXTENDED) != 0) sb.Append(" EXTENDED");
      if ((f & LLKHF_ALTDOWN) != 0) sb.Append(" ALTDOWN");
      if ((f & LLKHF_UP) != 0) sb.Append(" UP");
      return sb.ToString();
    }

    public static string MouseFlagsText(uint f)
    {
      var sb = new StringBuilder("0x" + f.ToString("X2"));
      if ((f & LLMHF_INJECTED) != 0) sb.Append(" INJECTED");
      if ((f & LLMHF_LOWER_IL_INJECTED) != 0) sb.Append(" LOWER_IL_INJECTED");
      return sb.ToString();
    }

    // dwExtraInfo whose upper 24 bits are 0xFF5157 marks mouse input generated from touch or pen (MI_WP_SIGNATURE).
    public static string ExtraText(ulong x)
    {
      string s = "0x" + x.ToString("X");
      if ((x & 0xFFFFFF00UL) == 0xFF515700UL) s += (x & 0x80) != 0 ? "(touch)" : "(pen)";
      return s;
    }

    public static string KeyMsgName(int msg)
    {
      switch (msg)
      {
        case WM_KEYDOWN: return "KEYDOWN";
        case WM_KEYUP: return "KEYUP";
        case WM_SYSKEYDOWN: return "SYSKEYDOWN";
        case WM_SYSKEYUP: return "SYSKEYUP";
      }
      return "0x" + msg.ToString("X4");
    }

    public static string MouseMsgName(int msg)
    {
      switch (msg)
      {
        case WM_MOUSEMOVE: return "MOVE";
        case WM_LBUTTONDOWN: return "LBUTTONDOWN";
        case WM_LBUTTONUP: return "LBUTTONUP";
        case WM_RBUTTONDOWN: return "RBUTTONDOWN";
        case WM_RBUTTONUP: return "RBUTTONUP";
        case WM_MBUTTONDOWN: return "MBUTTONDOWN";
        case WM_MBUTTONUP: return "MBUTTONUP";
        case WM_MOUSEWHEEL: return "WHEEL";
        case WM_MOUSEHWHEEL: return "HWHEEL";
        case WM_XBUTTONDOWN: return "XBUTTONDOWN";
        case WM_XBUTTONUP: return "XBUTTONUP";
      }
      return "0x" + msg.ToString("X4");
    }
  }
}
