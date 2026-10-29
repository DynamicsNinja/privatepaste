namespace PrivatePaste.Desktop.Platform;

/// <summary>
/// Whether the app that copied marked the copy as private (password managers do), judged from the marker formats on the
/// clipboard. Pure logic, testable without a clipboard. One method per platform, so each OS keeps its own convention.
/// </summary>
public static class PrivateCopyMarkers
{
    public const string ExcludeFromMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    public const string ViewerIgnore = "Clipboard Viewer Ignore";
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";

    /// <summary>The registered format names worth looking at; nothing else is read from the clipboard for this.</summary>
    public static readonly IReadOnlyList<string> WindowsFormats =
        [ExcludeFromMonitor, ViewerIgnore, CanIncludeInHistory, CanUploadToCloud];

    /// <summary>
    /// <paramref name="formatsByName"/> holds every marker format present, with its bytes (null when present but unreadable).
    /// The first two mark a copy private by merely being there; the other two unless they hold a readable non-zero DWORD
    /// (a present marker we cannot read fails closed: the copy must not reach the server).
    /// </summary>
    public static bool IsPrivate(IReadOnlyDictionary<string, byte[]?> formatsByName)
    {
        if (formatsByName.ContainsKey(ExcludeFromMonitor) || formatsByName.ContainsKey(ViewerIgnore)) return true;
        return BlocksDword(formatsByName, CanIncludeInHistory) || BlocksDword(formatsByName, CanUploadToCloud);
    }

    public const string MacConcealedType = "org.nspasteboard.ConcealedType";
    public const string MacTransientType = "org.nspasteboard.TransientType";
    public const string LinuxPasswordHint = "x-kde-passwordManagerHint";

    /// <summary>nspasteboard.org convention: password managers and the like add one of these types next to the text.</summary>
    public static bool IsPrivateMac(IEnumerable<string> types) =>
        types.Any(t => t == MacConcealedType || t == MacTransientType);

    /// <summary>
    /// KeePassXC, KDE and others add the <c>x-kde-passwordManagerHint</c> target with the value <c>secret</c>.
    /// <paramref name="readText"/> reads that target's text; null or a throw (present but unreadable) fails closed.
    /// Nothing is read unless the target is listed.
    /// </summary>
    public static bool IsPrivateLinux(IEnumerable<string> targets, Func<string, string?> readText)
    {
        if (!targets.Contains(LinuxPasswordHint)) return false;
        try { return IsSecret(readText(LinuxPasswordHint)); }
        catch (Exception) { return true; }
    }

    /// <inheritdoc cref="IsPrivateLinux"/>
    public static async Task<bool> IsPrivateLinuxAsync(IEnumerable<string> targets, Func<string, Task<string?>> readTextAsync)
    {
        if (!targets.Contains(LinuxPasswordHint)) return false;
        try { return IsSecret(await readTextAsync(LinuxPasswordHint)); }
        catch (Exception) { return true; }
    }

    private static bool IsSecret(string? hint) => hint is null || hint.Trim() == "secret";

    private static bool BlocksDword(IReadOnlyDictionary<string, byte[]?> formats, string name) =>
        formats.TryGetValue(name, out var bytes) && !(bytes is { Length: >= 4 } && BitConverter.ToUInt32(bytes, 0) != 0);   // little-endian on every supported host
}
