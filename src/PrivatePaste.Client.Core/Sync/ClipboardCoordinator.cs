using System.Text;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Images;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Sync;

/// <summary>
/// Decides what happens when the user copies something and when a clip arrives from another device.
/// <see cref="UnauthorizedApiException"/> from an upload (live or flush) is NOT swallowed, queued or dropped: it propagates
/// to the caller (sync engine/host), which signs out. A queued item stays queued; a live copy that hit it is not retained.
/// </summary>
public sealed class ClipboardCoordinator(
    IClipboardAccess clipboard, PrivatePasteApi api, HistoryCache cache, OutboundQueue queue,
    EchoGuard echo, INotifier notifier, TimeProvider time, Func<bool> isPaused)
{
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxImageBytes = 20 * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task OnClipboardChangedAsync(CancellationToken ct)
    {
        if (isPaused()) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (isPaused()) return;
            var content = await clipboard.ReadAsync(ct);
            if (content is null) return;

            string hash;
            switch (content)
            {
                case ClipboardContent.Text t:
                    if (string.IsNullOrWhiteSpace(t.Value)) return;
                    hash = ContentHash.ForText(t.Value);
                    break;
                case ClipboardContent.Image i:
                    try { hash = ImageHashing.HashPng(i.Png); }
                    catch (InvalidDataException) { return; }
                    break;
                default:
                    return;
            }

            if (echo.IsEcho(hash)) return;

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

            var copiedAt = time.GetUtcNow();
            try
            {
                var result = await UploadAsync(content, copiedAt, backfill: false, ct);
                cache.Upsert(result);
                cache.Save();
            }
            catch (HttpRequestException)
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

    public async Task FlushQueueAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (queue.TryPeek(out var item))
            {
                try
                {
                    var result = await UploadAsync(item.Content, item.CopiedAt, backfill: true, ct);
                    cache.Upsert(result);
                    cache.Save();
                    queue.Dequeue();
                }
                catch (HttpRequestException)
                {
                    return;
                }
                catch (ApiException e) when (e is not UnauthorizedApiException)
                {
                    notifier.Notify($"Server rejected a queued clip ({(int)e.Status}); it was dropped.");
                    queue.Dequeue();
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyRemoteAsync(ClipDto clip, CancellationToken ct)
    {
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

        echo.Remember(clip.Hash);
        await clipboard.WriteAsync(content, ct);
        cache.MarkApplied(clip);
        cache.Save();
    }

    private Task<ClipDto> UploadAsync(ClipboardContent content, DateTimeOffset copiedAt, bool backfill, CancellationToken ct) =>
        content switch
        {
            ClipboardContent.Text t => api.UploadTextAsync(t.Value, copiedAt, backfill, ct),
            ClipboardContent.Image i => api.UploadImageAsync(i.Png, copiedAt, backfill, ct),
            _ => throw new ArgumentException("Unsupported content", nameof(content)),
        };
}
