using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform;

/// <summary>
/// Placeholder until the per-OS clipboard exists (Task 8 replaces it on Windows): never raises Changed,
/// reads nothing, and ignores writes.
/// </summary>
public sealed class NullClipboard : IClipboardAccess
{
    public event EventHandler? Changed { add { } remove { } }

    public Task<ClipboardContent?> ReadAsync(CancellationToken ct) => Task.FromResult<ClipboardContent?>(null);

    public Task WriteAsync(ClipboardContent content, CancellationToken ct) => Task.CompletedTask;
}
