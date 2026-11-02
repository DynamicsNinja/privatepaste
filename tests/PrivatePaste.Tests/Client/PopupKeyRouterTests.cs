using PrivatePaste.Desktop.Input;
using SharpHook.Data;

namespace PrivatePaste.Tests.Client;

public class PopupKeyRouterTests
{
    private static PopupKey Route(KeyCode key, EventMask mask = EventMask.None, string? text = null) =>
        PopupKeyRouter.Route(key, mask, text);

    [Theory]
    [InlineData(KeyCode.VcUp, PopupKeyAction.Up)]
    [InlineData(KeyCode.VcDown, PopupKeyAction.Down)]
    [InlineData(KeyCode.VcPageUp, PopupKeyAction.PageUp)]
    [InlineData(KeyCode.VcPageDown, PopupKeyAction.PageDown)]
    [InlineData(KeyCode.VcEnter, PopupKeyAction.Paste)]
    [InlineData(KeyCode.VcNumPadEnter, PopupKeyAction.Paste)]
    [InlineData(KeyCode.VcEscape, PopupKeyAction.Close)]
    [InlineData(KeyCode.VcBackspace, PopupKeyAction.Backspace)]
    public void Popup_keys_are_handled(KeyCode key, PopupKeyAction action) =>
        Assert.Equal(new PopupKey(action), Route(key));

    [Theory]
    [InlineData("a")]
    [InlineData("A")]
    [InlineData("č")]
    [InlineData("1")]
    [InlineData(" ")]
    public void Printable_text_goes_to_the_search(string text) =>
        Assert.Equal(new PopupKey(PopupKeyAction.Type, text), Route(KeyCode.VcA, EventMask.None, text));

    [Fact]
    public void Shifted_text_goes_to_the_search() =>
        Assert.Equal(new PopupKey(PopupKeyAction.Type, "A"), Route(KeyCode.VcA, EventMask.LeftShift, "A"));

    [Fact]
    public void AltGr_text_goes_to_the_search() =>
        // AltGr arrives as Ctrl+Alt (e.g. @ on a Croatian keyboard).
        Assert.Equal(new PopupKey(PopupKeyAction.Type, "@"), Route(KeyCode.VcV, EventMask.RightAlt | EventMask.LeftCtrl, "@"));

    [Theory]
    [InlineData(KeyCode.VcLeftShift)]
    [InlineData(KeyCode.VcRightShift)]
    [InlineData(KeyCode.VcLeftControl)]
    [InlineData(KeyCode.VcRightControl)]
    [InlineData(KeyCode.VcLeftAlt)]
    [InlineData(KeyCode.VcRightAlt)]
    [InlineData(KeyCode.VcLeftMeta)]
    [InlineData(KeyCode.VcRightMeta)]
    [InlineData(KeyCode.VcCapsLock)]
    [InlineData(KeyCode.VcNumLock)]
    public void Modifier_keys_pass_through(KeyCode key) =>
        // The app you are in must see modifiers go down and up, or it would think one is stuck.
        Assert.Equal(new PopupKey(PopupKeyAction.Pass), Route(key));

    [Fact]
    public void Windows_key_shortcuts_pass_through() =>
        Assert.Equal(new PopupKey(PopupKeyAction.Pass), Route(KeyCode.VcD, EventMask.LeftMeta, "d"));

    [Theory]
    [InlineData(KeyCode.VcTab)]
    [InlineData(KeyCode.VcEscape)]
    public void Alt_Tab_and_Alt_Esc_pass_through(KeyCode key) =>
        Assert.Equal(new PopupKey(PopupKeyAction.Pass), Route(key, EventMask.LeftAlt));

    [Theory]
    [InlineData(KeyCode.VcC, EventMask.LeftCtrl, "\u0003")]   // Ctrl+C would copy in the app below
    [InlineData(KeyCode.VcX, EventMask.LeftCtrl, "\u0018")]   // Ctrl+X would cut
    [InlineData(KeyCode.VcV, EventMask.LeftCtrl, "\u0016")]
    [InlineData(KeyCode.VcF4, EventMask.LeftAlt, null)]        // Alt+F4 would close the app below
    [InlineData(KeyCode.VcF5, EventMask.None, null)]
    [InlineData(KeyCode.VcP, EventMask.LeftCtrl | EventMask.LeftShift, "")]   // Ctrl+Shift+P is not the pin key
    [InlineData(KeyCode.VcP, EventMask.LeftCtrl | EventMask.LeftAlt, null)]
    [InlineData(KeyCode.VcDelete, EventMask.None, null)]
    [InlineData(KeyCode.VcA, EventMask.LeftCtrl, "\u0001")]
    public void Other_keys_are_swallowed(KeyCode key, EventMask mask, string? text) =>
        // Nothing typed while the popup is open may act on the app below it.
        Assert.Equal(new PopupKey(PopupKeyAction.Swallow), Route(key, mask, text));

    [Fact]
    public void A_dead_key_with_no_text_is_swallowed() =>
        Assert.Equal(new PopupKey(PopupKeyAction.Swallow), Route(KeyCode.VcQuote, EventMask.None, null));

    [Theory]
    [InlineData(EventMask.None)]
    [InlineData(EventMask.LeftShift)]
    public void Tab_switches_tab(EventMask mask) =>
        Assert.Equal(new PopupKey(PopupKeyAction.SwitchTab), Route(KeyCode.VcTab, mask, "	"));

    [Fact]
    public void Ctrl_P_toggles_the_pin() =>
        Assert.Equal(new PopupKey(PopupKeyAction.TogglePin), Route(KeyCode.VcP, EventMask.LeftCtrl, ""));

    [Fact]
    public void Ctrl_S_saves() =>
        Assert.Equal(new PopupKey(PopupKeyAction.Save), Route(KeyCode.VcS, EventMask.LeftCtrl, "\u0013"));

    [Theory]
    [InlineData(EventMask.LeftCtrl | EventMask.LeftShift)]
    [InlineData(EventMask.RightCtrl | EventMask.RightShift)]
    public void Ctrl_Shift_S_is_swallowed(EventMask mask) =>
        Assert.Equal(new PopupKey(PopupKeyAction.Swallow), Route(KeyCode.VcS, mask, "\u0013"));

    [Theory]
    [InlineData(KeyCode.Vc1, 1)]
    [InlineData(KeyCode.Vc2, 2)]
    [InlineData(KeyCode.Vc3, 3)]
    [InlineData(KeyCode.Vc4, 4)]
    [InlineData(KeyCode.Vc5, 5)]
    public void Alt_digit_picks_a_filter(KeyCode key, int index)
    {
        Assert.Equal(new PopupKey(PopupKeyAction.Filter, Index: index), Route(key, EventMask.LeftAlt, "1"));
        Assert.Equal(new PopupKey(PopupKeyAction.Filter, Index: index), Route(key, EventMask.RightAlt, null));
    }

    [Theory]
    [InlineData(KeyCode.Vc6, EventMask.LeftAlt)]                      // only five chips
    [InlineData(KeyCode.Vc0, EventMask.LeftAlt)]
    [InlineData(KeyCode.Vc1, EventMask.LeftAlt | EventMask.LeftShift)]
    [InlineData(KeyCode.Vc1, EventMask.LeftCtrl)]                     // Ctrl+1 would switch tabs in the app below
    [InlineData(KeyCode.VcNumPad1, EventMask.LeftAlt)]                // Alt+numpad types Alt codes
    public void Other_digit_shortcuts_are_swallowed(KeyCode key, EventMask mask) =>
        Assert.Equal(new PopupKey(PopupKeyAction.Swallow), Route(key, mask, null));

    [Fact]
    public void Plain_digits_still_type_and_AltGr_digits_type_their_character()
    {
        Assert.Equal(new PopupKey(PopupKeyAction.Type, "1"), Route(KeyCode.Vc1, EventMask.None, "1"));
        // AltGr+1 is "~" on a Croatian keyboard and arrives as Ctrl+Alt: still text, not a filter.
        Assert.Equal(new PopupKey(PopupKeyAction.Type, "~"), Route(KeyCode.Vc1, EventMask.LeftCtrl | EventMask.RightAlt, "~"));
    }

    [Fact]
    public void Plain_p_still_types() =>
        Assert.Equal(new PopupKey(PopupKeyAction.Type, "p"), Route(KeyCode.VcP, EventMask.None, "p"));
}
