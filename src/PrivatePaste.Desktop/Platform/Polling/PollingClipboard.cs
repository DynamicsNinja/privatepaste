using System.Security.Cryptography;
using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform.Polling;

/// <summary>Reads and writes the real clipboard. Implementations may assume nothing about the calling thread.</summary>
public interface IClipboardBackend
{
    Task<ClipboardContent?> ReadAsync(CancellationToken ct);
    Task WriteAsync(ClipboardContent content, CancellationToken ct);
}

/// <summary>
/// macOS/Linux have no cheap "clipboard changed" signal in Avalonia, so this polls (default every 500 ms) and raises
/// <see cref="Changed"/> when the content differs from the last seen: text by value, images by a hash of the PNG bytes.
/// The first read is the baseline (no event), an emptied clipboard is not a change, and a failing read is skipped.
/// </summary>
public sealed class PollingClipboard(IClipboardBackend backend, TimeSpan? interval = null) : IClipboardAccess, IDisposable
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMilliseconds(500);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private string? _last;
    private bool _baselined;
    private int _started;

    public event EventHandler? Changed;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(PollLoopAsync);
    }

    public Task<ClipboardContent?> ReadAsync(CancellationToken ct) => backend.ReadAsync(ct);

    public Task WriteAsync(ClipboardContent content, CancellationToken ct) => backend.WriteAsync(content, ct);

    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { await PollOnceAsync(); }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
                catch (Exception) { /* a locked or unavailable clipboard: try again next tick */ }
            }
            while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException) { }
    }

    private async Task PollOnceAsync()
    {
        var content = await backend.ReadAsync(_cts.Token);
        if (content is null) return;
        var fingerprint = Fingerprint(content);
        bool changed;
        lock (_gate)
        {
            changed = _baselined && fingerprint != _last;
            _last = fingerprint;
            _baselined = true;
        }
        if (!changed) return;
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { /* a faulty subscriber must not stop the poller */ }
    }

    private static string Fingerprint(ClipboardContent content) => content switch
    {
        ClipboardContent.Text t => "t:" + t.Value,
        ClipboardContent.Image i => "i:" + Convert.ToHexString(SHA256.HashData(i.Png)),
        _ => "?",
    };

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
