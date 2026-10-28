using SharpHook.Data;

namespace PrivatePaste.Desktop.Input;

/// <summary>
/// A global hotkey such as "Ctrl+Shift+V": at least one modifier plus exactly one non-modifier key (SharpHook key code).
/// Parsing is case-insensitive and accepts Ctrl/Control, Shift, Alt/Option and Cmd/Command/Meta/Win/Super.
/// </summary>
public sealed record HotkeyGesture(bool Ctrl, bool Shift, bool Alt, bool Meta, KeyCode Key)
{
    private static readonly HashSet<KeyCode> ModifierKeys =
    [
        KeyCode.VcLeftShift, KeyCode.VcRightShift, KeyCode.VcLeftControl, KeyCode.VcRightControl,
        KeyCode.VcLeftAlt, KeyCode.VcRightAlt, KeyCode.VcLeftMeta, KeyCode.VcRightMeta,
        KeyCode.VcFunction, KeyCode.VcUndefined,
    ];

    public static bool TryParse(string text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool ctrl = false, shift = false, alt = false, meta = false;
        KeyCode? key = null;
        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0) return false;
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    if (ctrl) return false;
                    ctrl = true;
                    break;
                case "shift":
                    if (shift) return false;
                    shift = true;
                    break;
                case "alt" or "option":
                    if (alt) return false;
                    alt = true;
                    break;
                case "cmd" or "command" or "meta" or "win" or "super":
                    if (meta) return false;
                    meta = true;
                    break;
                default:
                    if (key is not null || ParseKey(token) is not { } k) return false;
                    key = k;
                    break;
            }
        }

        if (key is null || !(ctrl || shift || alt || meta)) return false;
        gesture = new HotkeyGesture(ctrl, shift, alt, meta, key.Value);
        return true;
    }

    /// <summary>Canonical form: modifiers in the order Ctrl, Alt, Shift, Cmd/Meta, then the key ("Ctrl+Shift+V").</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Meta) parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Meta");
        parts.Add(Key.ToString()["Vc".Length..]);
        return string.Join('+', parts);
    }

    private static KeyCode? ParseKey(string token)
    {
        // Letters and digits only: Enum.TryParse would otherwise also accept numbers and comma-separated lists.
        if (!token.All(char.IsAsciiLetterOrDigit)) return null;
        if (token.Equals("esc", StringComparison.OrdinalIgnoreCase)) token = "Escape";
        if (!Enum.TryParse<KeyCode>("Vc" + token, ignoreCase: true, out var key)
            || !Enum.IsDefined(key) || ModifierKeys.Contains(key))
            return null;
        return key;
    }
}
