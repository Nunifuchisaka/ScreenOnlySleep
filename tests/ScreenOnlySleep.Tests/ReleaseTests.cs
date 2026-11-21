using Xunit;
using static ScreenOnlySleep.InputFilter;

namespace ScreenOnlySleep.Tests
{
  public class InputFilterTests
  {
    [Theory]
    [InlineData(0x41u)]   // A
    [InlineData(0x7Bu)]   // F12
    [InlineData(0x11u)]   // Ctrl
    [InlineData(0x20u)]   // Space
    [InlineData(0x7Bu - 1)] // F11
    [InlineData(0x88u)]   // just after F24
    public void RealKeysMayRelease(uint vk)
    {
      Assert.Null(KeySkipReason(vk, 0));
      Assert.Null(KeySkipReason(vk, LLKHF_EXTENDED | LLKHF_ALTDOWN));
    }

    [Fact]
    public void InjectedKeysAreSkipped()
    {
      Assert.Equal("injected", KeySkipReason(0x41, LLKHF_INJECTED));
      Assert.Equal("injected(lower-il)", KeySkipReason(0x41, LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED));
      Assert.Equal("injected(lower-il)", KeySkipReason(0x41, LLKHF_LOWER_IL_INJECTED));
    }

    [Theory]
    [InlineData(0x7Cu, "F13")]
    [InlineData(0x7Eu, "F15")]
    [InlineData(0x87u, "F24")]
    [InlineData(0xFCu, "VK_NONAME")]
    [InlineData(0xFFu, "vk0xFF")]
    [InlineData(0u, "vk0")]
    public void UnusualKeysAreSkipped(uint vk, string reason)
    {
      Assert.Equal(reason, KeySkipReason(vk, 0));
    }

    [Fact]
    public void MouseInjectionIsSkipped()
    {
      Assert.Null(MouseSkipReason(0));
      Assert.Equal("injected", MouseSkipReason(LLMHF_INJECTED));
      Assert.Equal("injected(lower-il)", MouseSkipReason(LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED));
    }

    [Fact]
    public void ButtonIds()
    {
      bool down, up;
      Assert.Equal(1, ButtonId(WM_LBUTTONDOWN, 0, out down, out up)); Assert.True(down); Assert.False(up);
      Assert.Equal(1, ButtonId(WM_LBUTTONUP, 0, out down, out up)); Assert.False(down); Assert.True(up);
      Assert.Equal(2, ButtonId(WM_RBUTTONDOWN, 0, out down, out up));
      Assert.Equal(3, ButtonId(WM_MBUTTONUP, 0, out down, out up));
      Assert.Equal(4, ButtonId(WM_XBUTTONDOWN, 1u << 16, out down, out up));
      Assert.Equal(5, ButtonId(WM_XBUTTONUP, 2u << 16, out down, out up));
      Assert.Equal(0, ButtonId(WM_MOUSEMOVE, 0, out down, out up)); Assert.False(down); Assert.False(up);
      Assert.Equal(0, ButtonId(WM_MOUSEWHEEL, 120u << 16, out down, out up)); Assert.False(down);
      Assert.Equal(0, ButtonId(WM_MOUSEHWHEEL, 0, out down, out up));
    }

    [Fact]
    public void ExtraInfoMarksTouchAndPen()
    {
      Assert.Equal("0x0", ExtraText(0));
      Assert.Equal("0xFF515780(touch)", ExtraText(0xFF515780));
      Assert.Equal("0xFF515700(pen)", ExtraText(0xFF515700));
    }
  }

  public class ReleaseTrackerTests
  {
    const uint A = 0x41, B = 0x42;

    static ReleaseTracker Armed()
    {
      var t = new ReleaseTracker();
      t.Arm();
      return t;
    }

    [Fact]
    public void NothingReleasesBeforeArming()
    {
      var t = new ReleaseTracker();
      Assert.Equal(HookAction.Pass, t.OnKey(A, 0, WM_KEYDOWN).Action);
      Assert.Equal(HookAction.Pass, t.OnMouse(WM_LBUTTONDOWN, 0, 0).Action);
      Assert.Equal(TriggerKind.None, t.Triggered);
      Assert.False(t.Watching);
    }

    [Fact]
    public void RealKeyDownReleasesAndItsUpIsSwallowed()
    {
      var t = Armed();
      Assert.True(t.Watching);
      var r = t.OnKey(A, 0, WM_KEYDOWN);
      Assert.Equal(HookAction.Trigger, r.Action);
      Assert.True(r.Swallowed);
      Assert.Equal(TriggerKind.Input, t.Triggered);
      Assert.Equal("key", t.Reason);
      Assert.False(t.ReleaseDone);
      Assert.False(t.Watching);
      // key repeat while held
      Assert.Equal(HookAction.Swallow, t.OnKey(A, 0, WM_KEYDOWN).Action);
      Assert.Equal(HookAction.Swallow, t.OnKey(A, LLKHF_UP, WM_KEYUP).Action);
      Assert.True(t.ReleaseDone);
      // later real keys are no longer touched
      Assert.Equal(HookAction.Pass, t.OnKey(A, LLKHF_UP, WM_KEYUP).Action);
    }

    [Fact]
    public void SysKeyDownReleases()
    {
      var t = Armed();
      Assert.Equal(HookAction.Trigger, t.OnKey(0x12, LLKHF_ALTDOWN, WM_SYSKEYDOWN).Action);
      Assert.Equal(HookAction.Swallow, t.OnKey(0x12, LLKHF_UP, WM_SYSKEYUP).Action);
      Assert.True(t.ReleaseDone);
    }

    [Fact]
    public void KeyUpAloneDoesNotRelease()
    {
      var t = Armed();
      Assert.Equal(HookAction.Pass, t.OnKey(A, LLKHF_UP, WM_KEYUP).Action);
      Assert.Equal(TriggerKind.None, t.Triggered);
    }

    [Fact]
    public void SecondKeyPressedWhileReleasingIsSwallowedUntilItsUp()
    {
      var t = Armed();
      t.OnKey(A, 0, WM_KEYDOWN);
      Assert.Equal(HookAction.Swallow, t.OnKey(B, 0, WM_KEYDOWN).Action);
      Assert.Equal(HookAction.Swallow, t.OnKey(A, LLKHF_UP, WM_KEYUP).Action);
      Assert.False(t.ReleaseDone);
      Assert.Equal(HookAction.Swallow, t.OnKey(B, LLKHF_UP, WM_KEYUP).Action);
      Assert.True(t.ReleaseDone);
    }

    [Theory]
    [InlineData(LLKHF_INJECTED)]
    [InlineData(LLKHF_LOWER_IL_INJECTED)]
    [InlineData(LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)]
    public void InjectedKeysPassAndDoNotRelease(uint flags)
    {
      var t = Armed();
      var r = t.OnKey(A, flags, WM_KEYDOWN);
      Assert.Equal(HookAction.PassSkipped, r.Action);
      Assert.False(r.Swallowed);
      Assert.NotNull(r.SkipReason);
      Assert.Equal(TriggerKind.None, t.Triggered);
    }

    [Theory]
    [InlineData(0x7Cu)]
    [InlineData(0x7Eu)]
    [InlineData(0x87u)]
    [InlineData(0xFCu)]
    [InlineData(0xFFu)]
    [InlineData(0u)]
    public void UnusualKeysPassAndDoNotRelease(uint vk)
    {
      var t = Armed();
      Assert.Equal(HookAction.PassSkipped, t.OnKey(vk, 0, WM_KEYDOWN).Action);
      Assert.Equal(TriggerKind.None, t.Triggered);
    }

    [Fact]
    public void InjectedKeysPassEvenAfterRelease()
    {
      var t = Armed();
      t.OnKey(A, 0, WM_KEYDOWN);
      Assert.Equal(HookAction.PassSkipped, t.OnKey(B, LLKHF_INJECTED, WM_KEYDOWN).Action);
    }

    [Fact]
    public void MouseMoveAndWheelDoNotRelease()
    {
      var t = Armed();
      Assert.Equal(HookAction.Pass, t.OnMouse(WM_MOUSEMOVE, 0, 0).Action);
      Assert.Equal(HookAction.Pass, t.OnMouse(WM_MOUSEWHEEL, 120u << 16, 0).Action);
      Assert.Equal(HookAction.Pass, t.OnMouse(WM_MOUSEHWHEEL, 120u << 16, 0).Action);
      Assert.Equal(TriggerKind.None, t.Triggered);
    }

    [Fact]
    public void RealButtonDownReleasesAndItsUpIsSwallowed()
    {
      var t = Armed();
      Assert.Equal(HookAction.Trigger, t.OnMouse(WM_RBUTTONDOWN, 0, 0).Action);
      Assert.Equal("button", t.Reason);
      Assert.Equal(HookAction.Pass, t.OnMouse(WM_MOUSEMOVE, 0, 0).Action);
      Assert.Equal(HookAction.Swallow, t.OnMouse(WM_RBUTTONUP, 0, 0).Action);
      Assert.True(t.ReleaseDone);
      Assert.Equal(HookAction.Pass, t.OnMouse(WM_LBUTTONUP, 0, 0).Action);
    }

    [Fact]
    public void XButtonsAreTrackedSeparately()
    {
      var t = Armed();
      t.OnMouse(WM_XBUTTONDOWN, 1u << 16, 0);
      Assert.Equal(HookAction.Pass, t.OnMouse(WM_XBUTTONUP, 2u << 16, 0).Action);
      Assert.False(t.ReleaseDone);
      Assert.Equal(HookAction.Swallow, t.OnMouse(WM_XBUTTONUP, 1u << 16, 0).Action);
      Assert.True(t.ReleaseDone);
    }

    [Fact]
    public void InjectedClicksPassAndDoNotRelease()
    {
      var t = Armed();
      Assert.Equal(HookAction.PassSkipped, t.OnMouse(WM_LBUTTONDOWN, 0, LLMHF_INJECTED).Action);
      Assert.Equal(HookAction.PassSkipped, t.OnMouse(WM_LBUTTONUP, 0, LLMHF_INJECTED).Action);
      Assert.Equal(HookAction.PassSkipped, t.OnMouse(WM_LBUTTONDOWN, 0, LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED).Action);
      Assert.Equal(TriggerKind.None, t.Triggered);
    }

    [Fact]
    public void TimerReleaseLeavesRealInputAlone()
    {
      var t = Armed();
      Assert.True(t.TriggerByTimer("test"));
      Assert.Equal(TriggerKind.Timer, t.Triggered);
      Assert.Equal("test", t.Reason);
      Assert.Equal(HookAction.Pass, t.OnKey(A, 0, WM_KEYDOWN).Action);
      Assert.Equal(HookAction.Pass, t.OnMouse(WM_LBUTTONDOWN, 0, 0).Action);
      Assert.False(t.TriggerByTimer("timeout"));
      Assert.Equal("test", t.Reason);
    }

    [Fact]
    public void InputReleaseWinsOverALaterTimer()
    {
      var t = Armed();
      t.OnKey(A, 0, WM_KEYDOWN);
      Assert.False(t.TriggerByTimer("timeout"));
      Assert.Equal(TriggerKind.Input, t.Triggered);
      Assert.Equal("key", t.Reason);
    }

    [Fact]
    public void ConsecutiveSessionsDoNotShareState()
    {
      var first = Armed();
      first.OnKey(A, 0, WM_KEYDOWN);
      var second = new ReleaseTracker();
      Assert.Equal(TriggerKind.None, second.Triggered);
      Assert.Equal(HookAction.Pass, second.OnKey(A, LLKHF_UP, WM_KEYUP).Action);   // the first session's up is not the second's
      second.Arm();
      Assert.Equal(HookAction.Trigger, second.OnKey(B, 0, WM_KEYDOWN).Action);
    }
  }

  public class SkipRecorderTests
  {
    [Fact]
    public void DetailsAreCappedPerSignatureAndInTotal()
    {
      var r = new SkipRecorder(3, 5, 30);
      int detailed = 0;
      for (int i = 0; i < 10; i++) if (r.Record("a")) detailed++;
      Assert.Equal(3, detailed);
      for (int i = 0; i < 10; i++) if (r.Record("b")) detailed++;
      Assert.Equal(5, detailed);   // total cap
      Assert.Equal(20, r.Total);
      Assert.Equal(5, r.DetailCount);
      Assert.Contains("a x10", r.Summary());
      Assert.Contains("b x10", r.Summary());
    }

    [Fact]
    public void SignaturesBeyondTheLimitAreGrouped()
    {
      var r = new SkipRecorder(3, 40, 2);
      r.Record("a");
      r.Record("b");
      r.Record("c");
      r.Record("d");
      Assert.Equal(3, r.SignatureCount);
      Assert.Contains(SkipRecorder.OtherSignature + " x2", r.Summary());
    }

    [Fact]
    public void EmptySummary()
    {
      Assert.Equal("ignored input: 0", new SkipRecorder().Summary());
    }
  }
}
