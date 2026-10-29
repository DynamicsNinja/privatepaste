using SharpHook.Data;

namespace PrivatePaste.Desktop.Input;

public enum PopupKeyAction { Pass, Swallow, Up, Down, PageUp, PageDown, Paste, Close, Backspace, Type, SwitchTab, TogglePin }

/// <summary>What a key press does while the popup is open; <see cref="Text"/> is set for <see cref="PopupKeyAction.Type"/>.</summary>
public readonly record struct PopupKey(PopupKeyAction Action, string? Text = null);

/// <summary>
/// Decides what a key press means while the popup is open on Windows. The popup never takes the focus there (so a text
/// box below keeps editing, caret and all), and the keyboard hook hands it every key press instead.
/// <para>
/// Popup keys and typed text are handled by the popup. Modifier keys, Windows-key shortcuts and Alt+Tab / Alt+Esc pass
/// through to the app below: it must see modifiers go down and up, and switching away closes the popup. Everything else is
/// swallowed, so nothing typed while the popup is open (Ctrl+C, Alt+F4, Delete, ...) can act on the app below it.
/// </para>
/// </summary>
public static class PopupKeyRouter
{
    public static PopupKey Route(KeyCode key, EventMask mask, string? text)
    {
        switch (key)
        {
            case KeyCode.VcLeftShift or KeyCode.VcRightShift or KeyCode.VcLeftControl or KeyCode.VcRightControl
                or KeyCode.VcLeftAlt or KeyCode.VcRightAlt or KeyCode.VcLeftMeta or KeyCode.VcRightMeta
                or KeyCode.VcCapsLock or KeyCode.VcNumLock or KeyCode.VcScrollLock:
                return new PopupKey(PopupKeyAction.Pass);
        }

        var ctrl = (mask & EventMask.Ctrl) != 0;
        var alt = (mask & EventMask.Alt) != 0;
        if ((mask & EventMask.Meta) != 0) return new PopupKey(PopupKeyAction.Pass);
        if (alt && !ctrl && key is KeyCode.VcTab or KeyCode.VcEscape) return new PopupKey(PopupKeyAction.Pass);

        var plain = !ctrl && !alt;
        switch (key)
        {
            case KeyCode.VcUp when plain: return new PopupKey(PopupKeyAction.Up);
            case KeyCode.VcDown when plain: return new PopupKey(PopupKeyAction.Down);
            case KeyCode.VcPageUp when plain: return new PopupKey(PopupKeyAction.PageUp);
            case KeyCode.VcPageDown when plain: return new PopupKey(PopupKeyAction.PageDown);
            case KeyCode.VcEnter or KeyCode.VcNumPadEnter when plain: return new PopupKey(PopupKeyAction.Paste);
            case KeyCode.VcEscape when plain: return new PopupKey(PopupKeyAction.Close);
            case KeyCode.VcBackspace when plain: return new PopupKey(PopupKeyAction.Backspace);
            case KeyCode.VcTab when plain: return new PopupKey(PopupKeyAction.SwitchTab);
            // Ctrl+P alone: with Shift or Alt it stays swallowed like every other shortcut.
            case KeyCode.VcP when ctrl && !alt && (mask & EventMask.Shift) == 0: return new PopupKey(PopupKeyAction.TogglePin);
        }

        // Plain or shifted text, or AltGr (which Windows reports as Ctrl+Alt). Control characters (Ctrl+letter) are not text.
        var altGr = ctrl && alt;
        if ((plain || altGr) && text is { Length: > 0 } && !text.Any(char.IsControl))
            return new PopupKey(PopupKeyAction.Type, text);
        return new PopupKey(PopupKeyAction.Swallow);
    }
}
