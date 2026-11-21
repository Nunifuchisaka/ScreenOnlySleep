using System;
using System.Windows.Forms;

namespace ScreenOnlySleep
{
  // Hidden top-level window: receives WM_HOTKEY and the broadcasts WM_DISPLAYCHANGE / WM_ENDSESSION.
  internal sealed class ControlWindow : NativeWindow
  {
    const int WM_HOTKEY = 0x0312, WM_DISPLAYCHANGE = 0x007E, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;

    public event Action<int> HotkeyPressed;
    public event Action DisplayChanged;
    public event Action EndingSession;

    public ControlWindow()
    {
      var cp = new CreateParams
      {
        Caption = "ScreenOnlySleepControl",
        Style = NativeMethods.WS_POPUP,
        ExStyle = NativeMethods.WS_EX_TOOLWINDOW,
      };
      CreateHandle(cp);
    }

    protected override void WndProc(ref Message m)
    {
      switch (m.Msg)
      {
        case WM_HOTKEY:
          HotkeyPressed?.Invoke(m.WParam.ToInt32());
          return;
        case WM_DISPLAYCHANGE:
          DisplayChanged?.Invoke();
          break;
        case WM_QUERYENDSESSION:
          m.Result = new IntPtr(1);
          return;
        case WM_ENDSESSION:
          if (m.WParam != IntPtr.Zero) EndingSession?.Invoke();
          m.Result = IntPtr.Zero;
          return;
      }
      base.WndProc(ref m);
    }
  }
}
