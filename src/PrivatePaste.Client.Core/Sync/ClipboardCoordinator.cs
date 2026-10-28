using System.Net;
using System.Text;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Images;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Sync;

/// <summary>
/// Decides what happens when the user copies something and when a clip arrives from another device.
/// Failure classes for uploads: <see cref="HttpRequestException"/>, any 5xx, 408 and 429 are transient (live copy is queued,
/// a flush stops and keeps the item); other 4xx (400, 413, ...) are rejections (item dropped, one notification);
/// <see cref="UnauthorizedApiException"/> is NOT swallowed, queued or dropped: it propagates to the caller (sync engine/host),
/// which signs out. A queued item stays queued; a live copy that hit it is not retained.
/// </summary>
public sealed class ClipboardCoordinator(
    IClipboardAccess clipboard, PrivatePasteApi api, HistoryCache cache, OutboundQueue queue,
    EchoGuard echo, INotifier notifier, TimeProvider time, Func<bool> isPaused)
{
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxImageBytes = 20 * 1024 * 1024;

    /// <summary>After the app writes the clipboard, the first change seen within this span is its own write (OS may transform it).</summary>
    private static readonly TimeSpan OwnWriteWindow = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastLocalHash;
    private DateTimeOffset? _ownWriteAt;
    private bool _saveFailureNotified;

    public async Task OnClipboardChangedAsync(CancellationToken ct)
    {
        if (isPaused()) return;
        // Read before waiting on the gate so copies made during a slow upload are each captured, in order.
        var content = await clipboard.ReadAsync(ct).ConfigureAwait(false);
        if (content is null) return;
        var readAt = time.GetUtcNow();

        // Ordering of captures relies on SemaphoreSlim granting the gate to async waiters in FIFO order.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (isPaused()) return;

            // Cheap checks first: whitespace, then size, so oversized items never get decoded or hashed.
            if (content is ClipboardContent.Text { Value: var v } && string.IsNullOrWhiteSpace(v)) return;
            if (OversizeFingerprint(content) is { } fingerprint)
            {
                if (fingerprint == _lastLocalHash) return;   // same oversized content again: already notified
                _lastLocalHash = fingerprint;
                notifier.Notify(content is ClipboardContent.Text
                    ? "Clip too large to sync (text over 1 MB)."
                    : "Image too large to sync (over 20 MB).");
                return;
            }

            string hash;
            try
            {
                hash = content switch
                {
                    ClipboardContent.Text t => ContentHash.ForText(t.Value),
                    ClipboardContent.Image i => ImageHashing.HashPng(i.Png),
                    _ => throw new InvalidDataException(),
                };
            }
            catch (InvalidDataException)
            {
                return;
            }

            // The first image change right after our own image write is that write (the OS may have transformed it).
            // Measured from when the clipboard was read, so a copy read before the write (and queued behind it) is not mistaken.
            if (_ownWriteAt is { } at)
            {
                if (readAt - at > OwnWriteWindow) _ownWriteAt = null;
                else if (readAt >= at && content is ClipboardContent.Image)
                {
                    _ownWriteAt = null;
                    _lastLocalHash = hash;
                    return;
                }
            }

            if (hash == _lastLocalHash) return;
            if (echo.IsEcho(hash)) { _lastLocalHash = hash; return; }

            var copiedAt = time.GetUtcNow();
            try
            {
                var result = await UploadAsync(content, copiedAt, backfill: false, ct).ConfigureAwait(false);
                _lastLocalHash = hash;
                cache.Upsert(result);
                TrySaveCache();
            }
            catch (Exception e) when (IsTransient(e))
            {
                queue.Enqueue(content, copiedAt);
                _lastLocalHash = hash;
            }
            catch (ApiException e) when (e is not UnauthorizedApiException)
            {
                notifier.Notify($"Server rejected the clip ({(int)e.Status}).");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Remembers what is on the clipboard right now without uploading it, so content copied while paused is never sent.
    /// Call this BEFORE un-pausing (while <c>isPaused()</c> is still true), so no change event can slip in between.
    /// </summary>
    public async Task OnResumedAsync(CancellationToken ct)
    {
        var content = await clipboard.ReadAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _lastLocalHash = null;
            try
            {
                _lastLocalHash = content switch
                {
                    null => null,
                    ClipboardContent.Text t when string.IsNullOrWhiteSpace(t.Value) => null,
                    _ when OversizeFingerprint(content) is { } f => f,
                    ClipboardContent.Text t => ContentHash.ForText(t.Value),
                    ClipboardContent.Image i => ImageHashing.HashPng(i.Png),
                    _ => null,
                };
            }
            catch (InvalidDataException) { }
        }
        finally { _gate.Release(); }
    }

    public async Task FlushQueueAsync(CancellationToken ct)
    {
        if (isPaused()) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (!isPaused() && queue.TryPeek(out var item))
            {
                ClipDto result;
                try
                {
                    result = await UploadAsync(item.Content, item.CopiedAt, backfill: true, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (IsTransient(e))
                {
                    return;
                }
                catch (ApiException e) when (e is not UnauthorizedApiException)
                {
                    notifier.Notify($"Server rejected a queued clip ({(int)e.Status}); it was dropped.");
                    queue.Dequeue();
                    continue;
                }
                queue.Dequeue();   // uploaded: never re-send it, even if the local cache cannot be saved
                cache.Upsert(result);
                TrySaveCache();
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Writes a clip from another device to the clipboard. Throws if the clipboard write fails (non-auth, e.g. the
    /// clipboard is locked by another process) after undoing the echo bookkeeping: callers MUST catch it and decide
    /// whether to retry. Network errors fetching an image also propagate (see <see cref="PrivatePasteApi"/>).
    /// </summary>
    public async Task ApplyRemoteAsync(ClipDto clip, CancellationToken ct)
    {
        if (isPaused()) return;
        ClipboardContent content;
        if (clip.Kind == ClipKind.Image)
        {
            var png = await api.GetImageAsync(clip.Id, ct).ConfigureAwait(false);
            try { ImageHashing.HashPng(png); }
            catch (InvalidDataException) { return; }   // e.g. a captive-portal page: transient, skip without side effects
            content = new ClipboardContent.Image(png);
        }
        else
        {
            if (clip.Text is null) return;
            content = new ClipboardContent.Text(clip.Text);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (isPaused()) return;
            var previousHash = _lastLocalHash;
            echo.Remember(clip.Hash);
            _lastLocalHash = clip.Hash;
            _ownWriteAt = content is ClipboardContent.Image ? time.GetUtcNow() : null;   // text relies on the exact-hash echo
            try
            {
                await clipboard.WriteAsync(content, ct).ConfigureAwait(false);
            }
            catch
            {
                // Nothing was written: the user's next copy of this content must not be treated as our echo. Rethrow to the caller.
                echo.Forget(clip.Hash);
                _lastLocalHash = previousHash;
                _ownWriteAt = null;
                throw;
            }
            cache.MarkApplied(clip);
            TrySaveCache();
        }
        finally { _gate.Release(); }
    }

    private static bool IsTransient(Exception e) => e switch
    {
        HttpRequestException => true,
        UnauthorizedApiException => false,
        ApiException a => (int)a.Status >= 500 || a.Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        _ => false,
    };

    private void TrySaveCache()
    {
        try
        {
            cache.Save();
            _saveFailureNotified = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_saveFailureNotified) return;
            _saveFailureNotified = true;
            notifier.Notify("Could not save local history.");
        }
    }

    /// <summary>For oversized content: "oversize:" + SHA-256 of the raw bytes (no decoding); null when within limits.</summary>
    private static string? OversizeFingerprint(ClipboardContent content)
    {
        byte[] bytes;
        switch (content)
        {
            case ClipboardContent.Text t when Encoding.UTF8.GetByteCount(t.Value) > MaxTextBytes:
                bytes = Encoding.UTF8.GetBytes(t.Value);
                break;
            case ClipboardContent.Image i when i.Png.Length > MaxImageBytes:
                bytes = i.Png;
                break;
            default:
                return null;
        }
        return "oversize:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private Task<ClipDto> UploadAsync(ClipboardContent content, DateTimeOffset copiedAt, bool backfill, CancellationToken ct) =>
        content switch
        {
            ClipboardContent.Text t => api.UploadTextAsync(t.Value, copiedAt, backfill, ct),
            ClipboardContent.Image i => api.UploadImageAsync(i.Png, copiedAt, backfill, ct),
            _ => throw new ArgumentException("Unsupported content", nameof(content)),
        };
}
