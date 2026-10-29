namespace PrivatePaste.Desktop.Platform.Windows;

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
    /// The first two mark a copy private by merely being there; the other two only when they hold the DWORD 0.
    /// </summary>
    public static bool IsPrivate(IReadOnlyDictionary<string, byte[]?> formatsByName)
    {
        if (formatsByName.ContainsKey(ExcludeFromMonitor) || formatsByName.ContainsKey(ViewerIgnore)) return true;
        return IsZeroDword(formatsByName, CanIncludeInHistory) || IsZeroDword(formatsByName, CanUploadToCloud);
    }

    private static bool IsZeroDword(IReadOnlyDictionary<string, byte[]?> formats, string name) =>
        formats.TryGetValue(name, out var bytes) && bytes is { Length: >= 4 } && BitConverter.ToUInt32(bytes, 0) == 0;   // little-endian on every supported host
}
