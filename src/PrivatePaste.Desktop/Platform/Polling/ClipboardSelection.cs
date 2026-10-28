using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform.Polling;

/// <summary>
/// Which clipboard format wins when several are present: non-blank text first (office apps put text plus a bitmap),
/// then image/png, then blank text. The PNG is only read when needed.
/// </summary>
public static class ClipboardSelection
{
    public static ClipboardContent? Choose(string? text, Func<byte[]?> readPng)
    {
        if (!string.IsNullOrWhiteSpace(text)) return new ClipboardContent.Text(text);
        if (readPng() is { Length: > 0 } png) return new ClipboardContent.Image(png);
        return string.IsNullOrEmpty(text) ? null : new ClipboardContent.Text(text);
    }
}
