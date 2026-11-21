using System.Collections.Generic;
using System.Threading;

namespace ScreenOnlySleep
{
  internal enum HookAction
  {
    Pass,         // let the event through
    PassSkipped,  // let it through; it is input we deliberately ignore (see SkipReason)
    Trigger,      // this event ended the session; swallow it
    Swallow,      // part of the input that ended the session (later downs and the matching ups); swallow it
  }

  internal struct HookResult
  {
    public HookAction Action;
    public string SkipReason;
    public bool Swallowed { get { return Action == HookAction.Trigger || Action == HookAction.Swallow; } }
    public static HookResult Of(HookAction a, string reason = null) { return new HookResult { Action = a, SkipReason = reason }; }
  }

  internal enum TriggerKind { None = 0, Timer = 1, Input = 2 }

  // One blank session's release logic. OnKey / OnMouse run on the hook thread; the rest may be called from any thread.
  internal sealed class ReleaseTracker
  {
    volatile bool armed;
    int triggered;   // TriggerKind, set exactly once
    volatile string reason;
    volatile bool releaseDone;
    readonly HashSet<int> heldKeys = new HashSet<int>();     // hook thread only
    readonly HashSet<int> heldButtons = new HashSet<int>();  // hook thread only

    public bool Armed { get { return armed; } }
    public void Arm() { armed = true; }
    public TriggerKind Triggered { get { return (TriggerKind)Volatile.Read(ref triggered); } }
    public string Reason { get { return reason; } }
    // True once every key and button swallowed after an input release has come back up.
    public bool ReleaseDone { get { return releaseDone; } }
    // True while ignored input is worth counting: armed and not yet released.
    public bool Watching { get { return armed && Triggered == TriggerKind.None; } }

    // Release by timer (test duration or the safety timeout). Returns false if already released.
    public bool TriggerByTimer(string why)
    {
      if (Interlocked.CompareExchange(ref triggered, (int)TriggerKind.Timer, 0) != 0) return false;
      reason = why;
      return true;
    }

    public HookResult OnKey(uint vk, uint flags, int msg)
    {
      bool down = InputFilter.IsKeyDown(msg), up = InputFilter.IsKeyUp(msg);
      string skip = InputFilter.KeySkipReason(vk, flags);
      if (skip != null) return HookResult.Of(HookAction.PassSkipped, skip);
      int id = (int)vk;
      if (Triggered == TriggerKind.Input)
      {
        if (down) { heldKeys.Add(id); releaseDone = false; return HookResult.Of(HookAction.Swallow); }
        if (up && heldKeys.Remove(id)) { CheckReleaseDone(); return HookResult.Of(HookAction.Swallow); }
        return HookResult.Of(HookAction.Pass);
      }
      if (armed && down && Interlocked.CompareExchange(ref triggered, (int)TriggerKind.Input, 0) == 0)
      {
        reason = "key";
        heldKeys.Add(id);
        return HookResult.Of(HookAction.Trigger);
      }
      return HookResult.Of(HookAction.Pass);
    }

    public HookResult OnMouse(int msg, uint mouseData, uint flags)
    {
      bool down, up;
      int id = InputFilter.ButtonId(msg, mouseData, out down, out up);
      string skip = InputFilter.MouseSkipReason(flags);
      if (skip != null) return HookResult.Of(HookAction.PassSkipped, skip);
      if (id == 0) return HookResult.Of(HookAction.Pass);   // move and wheel never release
      if (Triggered == TriggerKind.Input)
      {
        if (down) { heldButtons.Add(id); releaseDone = false; return HookResult.Of(HookAction.Swallow); }
        if (up && heldButtons.Remove(id)) { CheckReleaseDone(); return HookResult.Of(HookAction.Swallow); }
        return HookResult.Of(HookAction.Pass);
      }
      if (armed && down && Interlocked.CompareExchange(ref triggered, (int)TriggerKind.Input, 0) == 0)
      {
        reason = "button";
        heldButtons.Add(id);
        return HookResult.Of(HookAction.Trigger);
      }
      return HookResult.Of(HookAction.Pass);
    }

    void CheckReleaseDone()
    {
      if (heldKeys.Count == 0 && heldButtons.Count == 0) releaseDone = true;
    }
  }
}
