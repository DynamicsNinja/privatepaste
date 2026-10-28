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

    public async Task OnClipboardChangedAsync(CancellationToken ct)
    {
        if (isPaused()) return;
        // Read before waiting on the gate so copies made during a slow upload are each captured, in order.
        var content = await clipboard.ReadAsync(ct);
        if (content is null) return;

        await _gate.WaitAsync(ct);
        try
        {
            if (isPaused()) return;

            // Cheap checks first: whitespace, then size, so oversized items never get decoded or hashed.
            if (content is ClipboardContent.Text { Value: var v } && string.IsNullOrWhiteSpace(v)) return;
            if (content is ClipboardContent.Text tx && Encoding.UTF8.GetByteCount(tx.Value) > MaxTextBytes)
            {
                notifier.Notify("Clip too large to sync (text over 1 MB).");
                return;
            }
            if (content is ClipboardContent.Image im && im.Png.Length > MaxImageBytes)
            {
                notifier.Notify("Image too large to sync (over 20 MB).");
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
                _ownWriteAt = null;
                return;
            }

            // The first change right after our own write is that write (possibly transformed by the OS).
            if (_ownWriteAt is { } at)
            {
                _ownWriteAt = null;
                if (time.GetUtcNow() - at <= OwnWriteWindow)
                {
                    _lastLocalHash = hash;
                    return;
                }
            }

            if (hash == _lastLocalHash) return;
            _lastLocalHash = hash;
            if (echo.IsEcho(hash)) return;

            var copiedAt = time.GetUtcNow();
            try
            {
                var result = await UploadAsync(content, copiedAt, backfill: false, ct);
                cache.Upsert(result);
                TrySaveCache();
            }
            catch (Exception e) when (IsTransient(e))
            {
                queue.Enqueue(content, copiedAt);
            }
            catch (ApiException e) when (e is not UnauthorizedApiException)
            {
                notifier.Notify($"Server rejected the clip ({(int)e.Status}).");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Call after sync is resumed: remembers what is on the clipboard right now without uploading it, so content copied
    /// while paused is never sent.
    /// </summary>
    public async Task OnResumedAsync(CancellationToken ct)
    {
        var content = await clipboard.ReadAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            _lastLocalHash = null;
            try
            {
                _lastLocalHash = content switch
                {
                    ClipboardContent.Text t when !string.IsNullOrWhiteSpace(t.Value) => ContentHash.ForText(t.Value),
                    ClipboardContent.Image i when i.Png.Length <= MaxImageBytes => ImageHashing.HashPng(i.Png),
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
        await _gate.WaitAsync(ct);
        try
        {
            while (!isPaused() && queue.TryPeek(out var item))
            {
                ClipDto result;
                try
                {
                    result = await UploadAsync(item.Content, item.CopiedAt, backfill: true, ct);
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

    public async Task ApplyRemoteAsync(ClipDto clip, CancellationToken ct)
    {
        if (isPaused()) return;
        ClipboardContent content;
        if (clip.Kind == ClipKind.Image)
        {
            var png = await api.GetImageAsync(clip.Id, ct);
            try { ImageHashing.HashPng(png); }
            catch (InvalidDataException) { return; }   // e.g. a captive-portal page: transient, skip without side effects
            content = new ClipboardContent.Image(png);
        }
        else
        {
            if (clip.Text is null) return;
            content = new ClipboardContent.Text(clip.Text);
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (isPaused()) return;
            var previousHash = _lastLocalHash;
            echo.Remember(clip.Hash);
            _lastLocalHash = clip.Hash;
            _ownWriteAt = time.GetUtcNow();
            try
            {
                await clipboard.WriteAsync(content, ct);
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
        try { cache.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notifier.Notify("Could not save local history.");
        }
    }

    private Task<ClipDto> UploadAsync(ClipboardContent content, DateTimeOffset copiedAt, bool backfill, CancellationToken ct) =>
        content switch
        {
            ClipboardContent.Text t => api.UploadTextAsync(t.Value, copiedAt, backfill, ct),
            ClipboardContent.Image i => api.UploadImageAsync(i.Png, copiedAt, backfill, ct),
            _ => throw new ArgumentException("Unsupported content", nameof(content)),
        };
}
