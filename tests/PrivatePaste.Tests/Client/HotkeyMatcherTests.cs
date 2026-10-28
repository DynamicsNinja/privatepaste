using PrivatePaste.Desktop.Input;
using SharpHook.Data;

namespace PrivatePaste.Tests.Client;

public class HotkeyMatcherTests
{
    private static readonly HotkeyGesture Gesture = new(Ctrl: true, Shift: true, Alt: false, Meta: false, KeyCode.VcV);
    private const EventMask CtrlShift = EventMask.LeftCtrl | EventMask.LeftShift;

    private static HotkeyMatcher Create()
    {
        var s = new HotkeyMatcher { Gesture = Gesture };
        return s;
    }

    [Fact]
    public void Match_fires_once_and_auto_repeat_is_swallowed_until_key_up()
    {
        var s = Create();
        Assert.Equal(HotkeyDecision.Fire, s.Evaluate(KeyCode.VcV, CtrlShift, false));
        Assert.Equal(HotkeyDecision.Swallow, s.Evaluate(KeyCode.VcV, CtrlShift, false));
        Assert.Equal(HotkeyDecision.Swallow, s.Evaluate(KeyCode.VcV, CtrlShift, false));
        s.Released(KeyCode.VcV);
        Assert.Equal(HotkeyDecision.Fire, s.Evaluate(KeyCode.VcV, CtrlShift, false));
    }

    [Fact]
    public void Simulated_events_are_ignored()
    {
        var s = Create();
        Assert.Equal(HotkeyDecision.Ignore, s.Evaluate(KeyCode.VcV, CtrlShift, simulated: true));
        Assert.Equal(HotkeyDecision.Fire, s.Evaluate(KeyCode.VcV, CtrlShift, false));   // and did not arm the repeat latch
    }

    [Fact]
    public void Wrong_modifiers_or_key_are_ignored()
    {
        var s = Create();
        Assert.Equal(HotkeyDecision.Ignore, s.Evaluate(KeyCode.VcV, EventMask.LeftCtrl, false));
        Assert.Equal(HotkeyDecision.Ignore, s.Evaluate(KeyCode.VcV, CtrlShift | EventMask.LeftAlt, false));
        Assert.Equal(HotkeyDecision.Ignore, s.Evaluate(KeyCode.VcC, CtrlShift, false));
    }
}
