namespace PrivatePaste.Client.Core.Platform;

public interface IClipboardAccess
{
    /// <summary>Raised on any clipboard change (from the platform watcher).</summary>
    event EventHandler? Changed;

    /// <summary>Null when the clipboard holds nothing supported.</summary>
    Task<ClipboardContent?> ReadAsync(CancellationToken ct);

    Task WriteAsync(ClipboardContent content, CancellationToken ct);
}
