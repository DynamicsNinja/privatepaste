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
    private const int MaxBytes = 200;
    private const int MaxExtensionBytes = 32;
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
    /// cut to 200 UTF-16 units keeping the extension (the cut never splits a surrogate pair and the stem is re-trimmed);
    /// then cut to 200 UTF-8 bytes the same way (an extension over 32 bytes is cut with the stem).
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
            var keep = MaxLength - ext.Length;
            // Ports mirror this: never end the cut stem on the first half of a surrogate pair (step back one unit),
            // then trim trailing dots and spaces from the stem again; an empty stem becomes "file".
            if (char.IsHighSurrogate(s[keep - 1]) && char.IsLowSurrogate(s[keep])) keep--;
            var stem = s[..keep].TrimEnd('.', ' ');
            s = (stem.Length == 0 ? Fallback : stem) + ext;
        }

        // ext4, f2fs and APFS allow 255 bytes per name and 200 units of CJK are 600 bytes, so the UTF-8 length is capped at
        // 200 bytes too (room left for " (99)" and ".part"). The stem is cut by whole code points; an extension over 32 bytes
        // is cut with it as one stem; trailing dots and spaces are trimmed again and an empty stem becomes "file".
        if (Utf8Length(s) > MaxBytes)
        {
            var ext = ExtensionOf(s);
            if (Utf8Length(ext) > MaxExtensionBytes) ext = "";
            var stemEnd = s.Length - ext.Length;
            var budget = MaxBytes - Utf8Length(ext);
            int keep = 0, bytes = 0;
            while (keep < stemEnd)
            {
                var (units, size) = CodePointAt(s, keep);
                if (bytes + size > budget) break;
                keep += units;
                bytes += size;
            }
            var stem = s[..keep].TrimEnd('.', ' ');
            s = (stem.Length == 0 ? Fallback : stem) + ext;
        }
        return s;
    }

    private static int Utf8Length(string s)
    {
        var bytes = 0;
        for (var i = 0; i < s.Length;)
        {
            var (units, size) = CodePointAt(s, i);
            i += units;
            bytes += size;
        }
        return bytes;
    }

    // UTF-16 units and UTF-8 bytes of the code point at i, counted by hand so every port agrees: a surrogate pair is 4 bytes,
    // a lone surrogate 3 (the size of the replacement character an encoder writes for it).
    private static (int Units, int Bytes) CodePointAt(string s, int i)
    {
        var c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) return (2, 4);
        return (1, c < 0x80 ? 1 : c < 0x800 ? 2 : 3);
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

    /// <summary>
    /// "0 B", "1023 B", "1.5 KB", "12 KB", "3.4 MB". 1 KB = 1024 B. In KB and MB the value is rounded half up to one decimal
    /// and shown with it only when that is below 10 and not .0 ("1 KB", "9.9 MB", "10 MB"); from 10 up it is rounded to an
    /// integer. A KB value that rounds to 1024 is shown in MB ("1 MB"). Integer arithmetic only, so ports agree to the digit.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var (text, kbOverflow) = Scaled(bytes, 1024, "KB");
        return kbOverflow ? Scaled(bytes, 1024 * 1024, "MB").Text : text;
    }

    private static (string Text, bool KbOverflow) Scaled(long bytes, long unit, string name)
    {
        var tenths = (bytes * 10 + unit / 2) / unit;
        if (tenths < 100)
            return (tenths % 10 == 0 ? $"{tenths / 10} {name}" : $"{tenths / 10}.{tenths % 10} {name}", false);
        var whole = (bytes + unit / 2) / unit;
        return ($"{whole} {name}", name == "KB" && whole >= 1024);
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
