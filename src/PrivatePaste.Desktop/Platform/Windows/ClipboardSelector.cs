using System.Text;
using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform.Windows;

public enum ClipboardFormat { Png, DibV5, Dib, UnicodeText }

/// <summary>
/// Decides what the clipboard "contains" from the formats present. Text wins when it is not whitespace-only (Excel/Word put text
/// plus a synthesized bitmap on the clipboard); otherwise PNG, then DIBV5, then DIB. Pure logic, testable without a clipboard.
/// </summary>
public static class ClipboardSelector
{
    /// <summary>
    /// Copies the raw bytes of the formats <see cref="Select"/> may need, in its order of preference, and nothing else: no
    /// decoding or conversion, so it is cheap enough to run while the clipboard is held open.
    /// </summary>
    public static Dictionary<ClipboardFormat, byte[]> Capture(Func<ClipboardFormat, byte[]?> get)
    {
        var captured = new Dictionary<ClipboardFormat, byte[]>();
        if (get(ClipboardFormat.UnicodeText) is { } raw)
        {
            captured[ClipboardFormat.UnicodeText] = raw;
            if (!string.IsNullOrWhiteSpace(FromUtf16Z(raw))) return captured;
        }
        if (get(ClipboardFormat.Png) is { } png)
        {
            captured[ClipboardFormat.Png] = png;
            return captured;
        }
        foreach (var format in new[] { ClipboardFormat.DibV5, ClipboardFormat.Dib })
            if (get(format) is { } dib) captured[format] = dib;
        return captured;
    }

    /// <summary>Selects from bytes captured earlier (see <see cref="Capture"/>); DIB to PNG conversion happens here.</summary>
    public static ClipboardContent? Select(IReadOnlyDictionary<ClipboardFormat, byte[]> captured, Action<string>? log) =>
        Select(f => captured.GetValueOrDefault(f), log);

    public static ClipboardContent? Select(Func<ClipboardFormat, byte[]?> get, Action<string>? log)
    {
        if (get(ClipboardFormat.UnicodeText) is { } raw)
        {
            var text = FromUtf16Z(raw);
            if (!string.IsNullOrWhiteSpace(text)) return new ClipboardContent.Text(text);
        }

        if (get(ClipboardFormat.Png) is { } png) return new ClipboardContent.Image(png);

        foreach (var format in new[] { ClipboardFormat.DibV5, ClipboardFormat.Dib })
        {
            if (get(format) is not { } dib) continue;
            try { return new ClipboardContent.Image(DibConverter.ToPng(dib)); }
            catch (InvalidDataException e) { log?.Invoke("Skipped an unsupported clipboard bitmap: " + e.Message); }
        }
        return null;
    }

    internal static string FromUtf16Z(byte[] bytes)
    {
        var chars = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        var end = chars.IndexOf('\0');
        return end < 0 ? chars : chars[..end];
    }
}
