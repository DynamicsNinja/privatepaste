using System.Text;

namespace PrivatePaste.Desktop.Platform.Polling;

/// <summary>
/// File lists as macOS and Linux clipboards hold them: <c>file://</c> URIs, one per line in <c>text/uri-list</c> (RFC 2483:
/// CRLF, "#" comments) and in GNOME's <c>x-special/gnome-copied-files</c> after a "copy" or "cut" line; macOS puts one
/// <c>public.file-url</c> on each item. Only local files count: other schemes and other hosts are skipped. Pure logic.
/// </summary>
public static class FileUris
{
    private const string Scheme = "file://";

    public static IReadOnlyList<string> ParseUriList(string list)
    {
        var paths = new List<string>();
        foreach (var raw in list.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (ToPath(line) is { } path) paths.Add(path);
        }
        return paths;
    }

    /// <summary>"copy" or "cut", then one URI per line; anything else is not this format and reads as empty.</summary>
    public static IReadOnlyList<string> ParseGnomeCopiedFiles(string text)
    {
        var newline = text.IndexOf('\n');
        var operation = (newline < 0 ? text : text[..newline]).TrimEnd('\r').Trim();
        if (operation is not ("copy" or "cut") || newline < 0) return [];
        return ParseUriList(text[(newline + 1)..]);
    }

    /// <summary>The local path of a <c>file://</c> URI (percent-decoded as UTF-8), or null for anything else.</summary>
    public static string? ToPath(string uri)
    {
        if (!uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri[Scheme.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var host = rest[..slash];
        if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var path = Uri.UnescapeDataString(rest[slash..]);
            return path.Length > 1 && path.IndexOf('\0') < 0 ? path : null;
        }
        catch (Exception e) when (e is ArgumentException or UriFormatException) { return null; }
    }

    /// <summary><c>file://</c> plus the (absolute, Unix) path with each segment percent-encoded (UTF-8).</summary>
    public static string ToUri(string path) =>
        Scheme + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public static string ToUriList(IReadOnlyList<string> paths)
    {
        var list = new StringBuilder();
        foreach (var path in paths) list.Append(ToUri(path)).Append("\r\n");
        return list.ToString();
    }

    public static string ToGnomeCopiedFiles(IReadOnlyList<string> paths) =>
        "copy\n" + string.Join('\n', paths.Select(ToUri));
}
