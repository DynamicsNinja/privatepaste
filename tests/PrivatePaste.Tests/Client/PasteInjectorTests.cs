using PrivatePaste.Desktop.Input;

namespace PrivatePaste.Tests.Client;

public class PasteInjectorTests
{
    [Fact] public void Same_window_in_front_can_refocus() => Assert.True(PasteInjector.CanRefocus(previous: 100, foreground: 100));
    [Fact] public void No_foreground_window_is_a_transition_and_can_refocus() => Assert.True(PasteInjector.CanRefocus(100, 0));
    [Fact] public void Another_window_in_front_means_changed() => Assert.False(PasteInjector.CanRefocus(100, 200));
    [Fact] public void Nothing_captured_never_refocuses() => Assert.False(PasteInjector.CanRefocus(previous: 0, foreground: 0));
    [Fact] public void Nothing_captured_with_a_foreground_never_refocuses() => Assert.False(PasteInjector.CanRefocus(0, 200));
}
