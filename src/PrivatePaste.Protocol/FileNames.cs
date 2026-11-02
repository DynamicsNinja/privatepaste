using System.Globalization;
using System.Text;

namespace PrivatePaste.Protocol;

/// <summary>
/// File name rules shared by every client. A name in a payload comes from another device and is untrusted:
/// <see cref="Safe"/> turns it into a leaf name that can't leave the folder it is written to.
/// The vectors in docs/e2e-test-vectors.json ("safeFileName", "fileRowText") pin these rules for the other platforms.
/// </summary>
public static class FileNames
{
    private const int MaxLength = 200;
    private const string Fallback = "file";
    private const string Octet = "application/octet-stream";

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "application/pdf",
        ["txt"] = "text/plain",
        ["csv"] = "text/csv",
        ["json"] = "application/json",
        ["zip"] = "application/zip",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
        ["svg"] = "image/svg+xml",
        ["mp4"] = "video/mp4",
        ["mp3"] = "audio/mpeg",
        ["doc"] = "application/msword",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xls"] = "application/vnd.ms-excel",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["ppt"] = "application/vnd.ms-powerpoint",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    /// <summary>
    /// The part after the last / or \; control characters and <c>&lt;&gt;:"|?*</c> dropped; trailing dots and spaces trimmed;
    /// "file" when nothing usable is left; a Windows reserved device name (CON, NUL, COM1, ...) prefixed with _;
    /// cut to 200 UTF-16 units keeping the extension.
    /// </summary>
    public static string Safe(string name)
    {
        var leaf = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        var sb = new StringBuilder(leaf.Length);
        foreach (var c in leaf)
            if (c >= 0x20 && c != 0x7F && "<>:\"|?*".IndexOf(c) < 0) sb.Append(c);
        var s = sb.ToString().TrimEnd('.', ' ');
        if (s is "" or "." or "..") return Fallback;

        var dot = s.IndexOf('.');
        if (IsReserved(dot < 0 ? s : s[..dot])) s = "_" + s;

        if (s.Length > MaxLength)
        {
            var ext = ExtensionOf(s);
            if (ext.Length >= MaxLength) ext = "";
            s = s[..(MaxLength - ext.Length)] + ext;
        }
        return s;
    }

    /// <summary>
    /// <paramref name="safeName"/> if nothing by that name is <paramref name="taken"/>, else "name (2).ext", "name (3).ext", ...
    /// with the counter before the extension.
    /// </summary>
    public static string Unique(string safeName, Func<string, bool> taken)
    {
        if (!taken(safeName)) return safeName;
        var ext = ExtensionOf(safeName);
        var stem = safeName[..^ext.Length];
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>A MIME type from the extension, for labelling only; application/octet-stream when unknown.</summary>
    public static string MimeFor(string name)
    {
        var ext = ExtensionOf(name);
        return ext.Length > 1 && MimeByExtension.TryGetValue(ext[1..], out var mime) ? mime : Octet;
    }

    /// <summary>"0 B", "1023 B", "1.5 KB", "12 KB", "3.4 MB": 1 KB = 1024 B, one decimal below 10, none from 10 up.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var (value, unit) = bytes < 1024 * 1024 ? (bytes / 1024.0, "KB") : (bytes / (1024.0 * 1024), "MB");
        return string.Create(CultureInfo.InvariantCulture, $"{(value < 10 ? value.ToString("F1", CultureInfo.InvariantCulture) : Math.Round(value, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture))} {unit}");
    }

    /// <summary>The row text of a file clip: "report.pdf +2 more · 3.4 MB".</summary>
    public static string RowText(IReadOnlyList<ClipFileInfo> files)
    {
        if (files.Count == 0) return "";
        var total = files.Sum(f => f.Size);
        var more = files.Count > 1 ? $" +{files.Count - 1} more" : "";
        return $"{files[0].Name}{more} · {FormatSize(total)}";
    }

    // Ends with the last dot unless the dot starts the name (".bashrc" has no extension); "" when there is none.
    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[dot..] : "";
    }

    private static bool IsReserved(string stem)
    {
        if (stem.Length == 3)
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        return stem.Length == 4 && stem[3] is >= '1' and <= '9'
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }
}
