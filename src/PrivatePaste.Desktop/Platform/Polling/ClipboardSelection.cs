using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform.Polling;

/// <summary>
/// Which clipboard format wins when several are present: a file list first (file managers offer the names as text too),
/// then non-blank text (office apps put text plus a bitmap), then image/png, then blank text. The PNG is only read when needed.
/// </summary>
public static class ClipboardSelection
{
    public static ClipboardContent? Choose(string? text, Func<byte[]?> readPng, IReadOnlyList<string>? files = null)
    {
        if (files is { Count: > 0 }) return new ClipboardContent.Files(files);
        if (!string.IsNullOrWhiteSpace(text)) return new ClipboardContent.Text(text);
        if (readPng() is { Length: > 0 } png) return new ClipboardContent.Image(png);
        return string.IsNullOrEmpty(text) ? null : new ClipboardContent.Text(text);
    }
}
