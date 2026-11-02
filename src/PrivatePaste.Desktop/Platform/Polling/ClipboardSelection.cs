using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform.Polling;

/// <summary>
/// Which clipboard format wins when several are present: image/png next to a file list (apps that put a temp file next to
/// a copied image), then the file list (file managers offer the names as text too), then non-blank text (office apps put
/// text plus a bitmap), then image/png, then blank text. The PNG is only read when needed.
/// </summary>
public static class ClipboardSelection
{
    public static ClipboardContent? Choose(string? text, Func<byte[]?> readPng, IReadOnlyList<string>? files = null)
    {
        if (files is { Count: > 0 })
            return readPng() is { Length: > 0 } image ? new ClipboardContent.Image(image) : new ClipboardContent.Files(files);
        if (!string.IsNullOrWhiteSpace(text)) return new ClipboardContent.Text(text);
        if (readPng() is { Length: > 0 } png) return new ClipboardContent.Image(png);
        return string.IsNullOrEmpty(text) ? null : new ClipboardContent.Text(text);
    }
}
