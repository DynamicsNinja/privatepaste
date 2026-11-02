using System.Text;
using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform.Windows;

public enum ClipboardFormat { Png, DibV5, Dib, UnicodeText, HDrop }

/// <summary>
/// Decides what the clipboard "contains" from the formats present. A file list (CF_HDROP) wins over text (Explorer offers the
/// names as text too), but not over an image: chat apps and screenshot tools put a temp file next to a copied bitmap, and
/// Explorer never adds a bitmap to a file copy. Without files: text when it is not whitespace-only (Excel/Word put text plus
/// a synthesized bitmap on the clipboard); otherwise PNG, then DIBV5, then DIB. Pure logic, testable without a clipboard.
/// </summary>
public static class ClipboardSelector
{
    /// <summary>
    /// Copies the raw bytes of the formats <see cref="Select"/> may need, in its order of preference, and nothing else. No
    /// image decoding or conversion; the only work is reading the file list's names (a short walk over its bytes, to tell an
    /// empty or broken list from a real one), so it stays cheap enough to run while the clipboard is held open.
    /// </summary>
    public static Dictionary<ClipboardFormat, byte[]> Capture(Func<ClipboardFormat, byte[]?> get)
    {
        var captured = new Dictionary<ClipboardFormat, byte[]>();
        if (get(ClipboardFormat.HDrop) is { } drop && DropFiles.Parse(drop).Count > 0)
        {
            // Files, unless an image came with them; the names as text are never needed.
            captured[ClipboardFormat.HDrop] = drop;
            CaptureImage(get, captured);
            return captured;
        }
        if (get(ClipboardFormat.UnicodeText) is { } raw)
        {
            captured[ClipboardFormat.UnicodeText] = raw;
            if (!string.IsNullOrWhiteSpace(FromUtf16Z(raw))) return captured;
        }
        CaptureImage(get, captured);
        return captured;
    }

    private static void CaptureImage(Func<ClipboardFormat, byte[]?> get, Dictionary<ClipboardFormat, byte[]> captured)
    {
        if (get(ClipboardFormat.Png) is { } png)
        {
            captured[ClipboardFormat.Png] = png;
            return;
        }
        foreach (var format in new[] { ClipboardFormat.DibV5, ClipboardFormat.Dib })
            if (get(format) is { } dib) captured[format] = dib;
    }

    /// <summary>Selects from bytes captured earlier (see <see cref="Capture"/>); DIB to PNG conversion happens here.</summary>
    public static ClipboardContent? Select(IReadOnlyDictionary<ClipboardFormat, byte[]> captured, Action<string>? log) =>
        Select(f => captured.GetValueOrDefault(f), log);

    public static ClipboardContent? Select(Func<ClipboardFormat, byte[]?> get, Action<string>? log)
    {
        if (get(ClipboardFormat.HDrop) is { } drop && DropFiles.Parse(drop) is { Count: > 0 } paths)
            return SelectImage(get, log) ?? new ClipboardContent.Files(paths);

        if (get(ClipboardFormat.UnicodeText) is { } raw)
        {
            var text = FromUtf16Z(raw);
            if (!string.IsNullOrWhiteSpace(text)) return new ClipboardContent.Text(text);
        }

        return SelectImage(get, log);
    }

    private static ClipboardContent? SelectImage(Func<ClipboardFormat, byte[]?> get, Action<string>? log)
    {
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
