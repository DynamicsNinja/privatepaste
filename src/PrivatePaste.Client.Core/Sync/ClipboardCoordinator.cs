using System.Net;
using System.Security.Cryptography;
using System.Text;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Images;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Sync;

/// <summary>
/// Decides what happens when the user copies something and when a clip arrives from another device. Clips are sealed with
/// the current <c>codec</c> at upload time (so a queued copy goes out under whatever key is current when it is sent), and
/// what the server returns is opened before it reaches the cache.
/// Failure classes for uploads: <see cref="HttpRequestException"/>, any 5xx, 408 and 429 are transient (live copy is queued,
/// a flush counts an attempt, see <see cref="FlushQueueAsync"/>); other 4xx (400, 413, ...) are rejections (item dropped, one notification),
/// except a 410 for a queued clip copied before the history was cleared, which is dropped without one.
/// Over a server limit (<see cref="ClipTooLargeException"/>): the item is dropped and the exception's message shown.
/// A 409 "Encryption was reset" (<see cref="KeyResetException"/>): the queue is cleared (it was meant for the history the
/// reset wiped), <see cref="KeyReset"/> is raised once, and nothing is sent under that key again.
/// No codec (locked), a refused key, a key disposed mid-encode or a 426: nothing is sent; a live copy is queued and a flush
/// stops without counting an attempt.
/// <see cref="UnauthorizedApiException"/> is NOT swallowed, queued or dropped: it propagates to the caller (sync engine/host),
/// which signs out. A queued item stays queued; a live copy that hit it is not retained.
/// A copy marked private, or from an app <c>isBlockedApp</c> names, is ignored (see <see cref="OnClipboardChangedAsync"/>).
/// </summary>
public sealed class ClipboardCoordinator(
    IClipboardAccess clipboard, PrivatePasteApi api, Func<ClipCodec?> codec, HistoryCache cache, OutboundQueue queue,
    EchoGuard echo, INotifier notifier, TimeProvider time, Func<bool> isPaused, Func<string, bool>? isBlockedApp = null,
    Action<string>? log = null)
{
    /// <summary>How many source apps <see cref="RecentSourceApps"/> remembers.</summary>
    public const int RecentSourceAppCount = 10;

    /// <summary>The server's default text limit, used until (or unless) <c>/api/server-info</c> says otherwise.</summary>
    public const int MaxTextBytes = 1024 * 1024;
    /// <summary>The server's default image limit, used until (or unless) <c>/api/server-info</c> says otherwise.</summary>
    public const int MaxImageBytes = 20 * 1024 * 1024;
    public static readonly ServerLimitsDto DefaultLimits = new(MaxTextBytes / 1024, MaxImageBytes / (1024 * 1024));
    /// <summary>A queued clip that failed this many times (transiently) moves to the back of the queue.</summary>
    public const int RotateAfterAttempts = 5;
    /// <summary>A queued clip that failed this many times is dropped.</summary>
    public const int DropAfterAttempts = 10;

    /// <summary>After the app writes the clipboard, the first change seen within this span is its own write (OS may transform it).</summary>
    private static readonly TimeSpan OwnWriteWindow = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastLocalHash;
    private DateTimeOffset? _ownWriteAt;
    private bool _saveFailureNotified;
    private long _lastLocalCaptureTicks;   // UTC ticks; 0 = none
    private readonly List<string> _recentApps = [];
    private readonly Func<string, bool> _isBlockedApp = isBlockedApp ?? (_ => false);
    private volatile ServerLimitsDto? _limits;
    /// <summary>The key id the server refused as reset: nothing is sent under it again.</summary>
    private volatile string? _refusedKeyId;

    /// <summary>
    /// The server refused this device's vault key as reset (409). Raised once per refused key, after the queue was cleared;
    /// the sync engine stops on it and the host asks for the new passphrase. A throwing handler is ignored.
    /// </summary>
    public event EventHandler? KeyReset;

    /// <summary>
    /// When the user last copied something here that is (or will be) synced: the server time of a successful live upload,
    /// or the copy time of a clip queued offline; for a private or blocked copy (never synced), the time it was read.
    /// A catch-up never applies a clip that is not newer than this.
    /// </summary>
    public DateTimeOffset? LastLocalCaptureAt =>
        Interlocked.Read(ref _lastLocalCaptureTicks) is var t and not 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    /// <summary>Background thumbnail downloads for image clips (also used by the sync engine).</summary>
    public ThumbnailFetcher Thumbnails { get; } = new(api, cache, codec);

    /// <summary>True while copies made offline still wait in the queue (they are newer than anything a catch-up brings).</summary>
    public bool HasQueuedClips => queue.Count > 0;

    /// <summary>The distinct apps of the latest copies (private ones too), newest first; kept in memory only.</summary>
    public IReadOnlyList<string> RecentSourceApps { get { lock (_recentApps) return _recentApps.ToArray(); } }

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
            if (content.SourceApp is { Length: > 0 } app) RememberSourceApp(app);

            // A private or blocked copy never leaves the device: no upload, queue, cache or hash. The log says only that one
            // was skipped and why (never content, length or app), so the owner can check which password managers are caught.
            // It is still the newest thing on this clipboard, so a catch-up must not overwrite it with an older remote clip,
            // and the next copy of the previously synced content must count as a new copy.
            if (IsPrivateOrBlocked(content))
            {
                log?.Invoke(content.IsPrivate ? "Skipped a copy marked private." : "Skipped a copy from a blocked app.");
                RecordLocalCapture(readAt);
                _lastLocalHash = null;
                _ownWriteAt = null;   // our own write is no longer on the clipboard
                return;
            }

            // Cheap checks first: whitespace, then size, so oversized items never get decoded or hashed.
            if (content is ClipboardContent.Text { Value: var v } && string.IsNullOrWhiteSpace(v)) return;
            var limits = await LimitsAsync(ct).ConfigureAwait(false);
            if (Oversize(content, limits) is { } over)
            {
                if (over.Fingerprint == _lastLocalHash) return;   // same oversized content again: already notified
                _lastLocalHash = over.Fingerprint;
                notifier.Notify(over.Message);
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
            var c = UsableCodec();
            try
            {
                // No usable key (locked, or refused as reset): kept for when there is one, like an offline copy.
                if (c is null) throw new VaultKeyUnavailableException();
                var upload = await c.EncodeAsync(content, copiedAt, backfill: false, limits, ct).ConfigureAwait(false);
                var stored = await api.UploadClipAsync(upload, ct).ConfigureAwait(false);
                _lastLocalHash = hash;
                RecordLocalCapture(stored.LastCopiedAt);
                CacheUploaded(c, stored);
            }
            catch (ClipTooLargeException e)
            {
                _lastLocalHash = hash;   // the same content again is not notified again
                notifier.Notify(e.Message);
            }
            catch (KeyResetException)
            {
                OnKeyRefused(c!);
            }
            catch (InvalidDataException) { }   // hashed above, yet the full decode failed: nothing to send
            catch (Exception e) when (IsTransient(e) || e is VaultKeyUnavailableException or VaultKeyClosedException or UpdateRequiredException)
            {
                queue.Enqueue(content, copiedAt);
                RecordLocalCapture(copiedAt);
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
                    _ when IsPrivateOrBlocked(content) => null,   // never hashed
                    ClipboardContent.Text t when string.IsNullOrWhiteSpace(t.Value) => null,
                    _ when Oversize(content, _limits ?? DefaultLimits) is { } over => over.Fingerprint,
                    ClipboardContent.Text t => ContentHash.ForText(t.Value),
                    ClipboardContent.Image i => ImageHashing.HashPng(i.Png),
                    _ => null,
                };
            }
            catch (InvalidDataException) { }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Uploads queued clips in order, each at most once per call. A transient failure counts an attempt on the item:
    /// the first <see cref="RotateAfterAttempts"/>-1 stop the flush (the next one retries); from then on the item moves to
    /// the back so later items still go out; after <see cref="DropAfterAttempts"/> it is dropped with one notification.
    /// </summary>
    public async Task FlushQueueAsync(CancellationToken ct)
    {
        if (isPaused()) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var remaining = queue.Count; remaining > 0 && !isPaused() && queue.TryPeek(out var item); remaining--)
            {
                if (UsableCodec() is not { } c) return;   // locked or refused: the queue waits for a usable key
                ClipDto stored;
                try
                {
                    var limits = await LimitsAsync(ct).ConfigureAwait(false);
                    var upload = await c.EncodeAsync(item.Content, item.CopiedAt, backfill: true, limits, ct).ConfigureAwait(false);
                    stored = await api.UploadClipAsync(upload, ct).ConfigureAwait(false);
                }
                catch (KeyResetException)
                {
                    OnKeyRefused(c);
                    return;
                }
                // Nothing was sent: the item stays, without counting an attempt.
                catch (Exception e) when (e is VaultKeyClosedException or UpdateRequiredException)
                {
                    return;
                }
                catch (ClipTooLargeException e)
                {
                    queue.Dequeue();
                    notifier.Notify(e.Message);
                    continue;
                }
                catch (InvalidDataException)
                {
                    queue.Dequeue();
                    notifier.Notify("Couldn't read a queued image; it was dropped.");
                    continue;
                }
                catch (Exception e) when (IsTransient(e))
                {
                    var attempts = queue.RecordFailure();
                    if (attempts >= DropAfterAttempts)
                    {
                        queue.Dequeue();
                        notifier.Notify("Couldn't sync a clip after several tries.");
                        continue;
                    }
                    if (attempts < RotateAfterAttempts) return;
                    queue.MoveHeadToBack();
                    continue;
                }
                catch (ApiException e) when (e.Status == HttpStatusCode.Gone)
                {
                    // Copied before the history was cleared (or older than the auto-delete age): cleared stays cleared, quietly.
                    queue.Dequeue();
                    continue;
                }
                catch (ApiException e) when (e is not UnauthorizedApiException)
                {
                    notifier.Notify($"Server rejected a queued clip ({(int)e.Status}); it was dropped.");
                    queue.Dequeue();
                    continue;
                }
                queue.Dequeue();   // uploaded: never re-send it, even if the local cache cannot be saved
                CacheUploaded(c, stored);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Writes a decoded clip (see <see cref="ClipCodec.Decode"/>) from another device to the clipboard. Throws if the
    /// clipboard write fails (non-auth, e.g. the clipboard is locked by another process) after undoing the echo
    /// bookkeeping: callers MUST catch it and decide whether to retry. Network errors fetching an image also propagate (see
    /// <see cref="PrivatePasteApi"/>), and so does <see cref="VaultKeyClosedException"/> (the key went away mid-apply).
    /// Returns whether the clipboard was written: false when skipped (paused, an unknown kind, no codec for an image, an
    /// image that does not open or decode, a text clip without text), in which case callers must not paste.
    /// </summary>
    public async Task<bool> ApplyRemoteAsync(ClipDto clip, CancellationToken ct)
    {
        if (isPaused()) return false;
        if (clip.Kind == ClipKind.Unknown) return false;   // a newer app's kind, or one this key can't open
        ClipboardContent content;
        // The echo guard works on plain content hashes (what a copy here hashes to), not on the server's keyed hash.
        string contentHash;
        if (clip.Kind == ClipKind.Image)
        {
            if (codec() is not { } c) return false;
            var sealedPng = await api.GetBlobAsync(clip.Id, 0, ct).ConfigureAwait(false);
            byte[] png;
            try
            {
                png = await c.DecodeBlobAsync(sealedPng, ct).ConfigureAwait(false);
                contentHash = ImageHashing.HashPng(png);
            }
            // e.g. a captive-portal page: transient, skip without side effects
            catch (Exception e) when (e is CryptographicException or InvalidDataException) { return false; }
            content = new ClipboardContent.Image(png);
        }
        else
        {
            if (clip.Text is null) return false;
            content = new ClipboardContent.Text(clip.Text);
            contentHash = ContentHash.ForText(clip.Text);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (isPaused()) return false;
            var previousHash = _lastLocalHash;
            echo.Remember(contentHash);
            _lastLocalHash = contentHash;
            _ownWriteAt = content is ClipboardContent.Image ? time.GetUtcNow() : null;   // text relies on the exact-hash echo
            try
            {
                await clipboard.WriteAsync(content, ct).ConfigureAwait(false);
            }
            catch
            {
                // Nothing was written: the user's next copy of this content must not be treated as our echo. Rethrow to the caller.
                echo.Forget(contentHash);
                _lastLocalHash = previousHash;
                _ownWriteAt = null;
                throw;
            }
            cache.MarkApplied(clip);
            TrySaveCache();
            return true;
        }
        finally { _gate.Release(); }
    }

    private bool IsPrivateOrBlocked(ClipboardContent content) =>
        content.IsPrivate || (content.SourceApp is { Length: > 0 } app && _isBlockedApp(app));

    /// <summary>Moves <paramref name="app"/> to the front of the recent source apps, keeping at most <see cref="RecentSourceAppCount"/>.</summary>
    private void RememberSourceApp(string app)
    {
        lock (_recentApps)
        {
            _recentApps.Remove(app);
            _recentApps.Insert(0, app);
            if (_recentApps.Count > RecentSourceAppCount)
                _recentApps.RemoveRange(RecentSourceAppCount, _recentApps.Count - RecentSourceAppCount);
        }
    }

    private void RecordLocalCapture(DateTimeOffset at)
    {
        var ticks = at.UtcTicks;
        var seen = Interlocked.Read(ref _lastLocalCaptureTicks);
        while (ticks > seen)
        {
            var was = Interlocked.CompareExchange(ref _lastLocalCaptureTicks, ticks, seen);
            if (was == seen) return;
            seen = was;
        }
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

    /// <summary>
    /// For content over a limit: its message and a fingerprint, "oversize:" + SHA-256 of the raw bytes (no decoding);
    /// null when within limits. An image's pixel count comes from its header only.
    /// </summary>
    private static (string Fingerprint, string Message)? Oversize(ClipboardContent content, ServerLimitsDto limits)
    {
        byte[] bytes;
        string message;
        switch (content)
        {
            case ClipboardContent.Text t when Encoding.UTF8.GetByteCount(ContentHash.NormalizeText(t.Value)) > limits.TextKb * 1024L:
                bytes = Encoding.UTF8.GetBytes(t.Value);
                message = ClipCodec.TextTooLargeMessage(limits.TextKb);
                break;
            case ClipboardContent.Image i when i.Png.Length > limits.ImageMb * 1024L * 1024:
                bytes = i.Png;
                message = ImagePreparation.TooLargeMessage(limits.ImageMb);
                break;
            case ClipboardContent.Image i when ImagePreparation.IsOverPixelLimit(i.Png):
                bytes = i.Png;
                message = ImagePreparation.TooManyPixels;
                break;
            default:
                return null;
        }
        return ("oversize:" + Convert.ToHexString(SHA256.HashData(bytes)), message);
    }

    /// <summary>
    /// The server's limits, fetched once per session (one coordinator). Offline, or on any failure, the defaults stand in
    /// for this call only: the upload itself then fails and queues, and the flush asks again.
    /// </summary>
    private async Task<ServerLimitsDto> LimitsAsync(CancellationToken ct)
    {
        if (_limits is { } known) return known;
        try
        {
            var limits = (await api.GetServerInfoAsync(ct).ConfigureAwait(false))?.Limits ?? DefaultLimits;
            _limits = limits;
            return limits;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return DefaultLimits; }
    }

    /// <summary>The current codec, unless there is none (locked) or the server refused its key as reset.</summary>
    private ClipCodec? UsableCodec() => codec() is { } c && c.KeyId != _refusedKeyId ? c : null;

    /// <summary>
    /// Opens what the server stored and caches it. A key disposed meanwhile (a sign-out, or a reset handled at the same
    /// time) only skips the cache: the clip went out.
    /// </summary>
    private void CacheUploaded(ClipCodec c, ClipDto stored)
    {
        ClipDto clip;
        try { clip = c.Decode(stored); }
        catch (VaultKeyClosedException) { return; }
        cache.KeyId = c.KeyId;
        cache.Upsert(clip, fromSync: false);
        Thumbnails.Request(clip);
        TrySaveCache();
    }

    /// <summary>The server refused <paramref name="c"/>'s key as reset: drop the queue and say so once. Caller holds the gate.</summary>
    private void OnKeyRefused(ClipCodec c)
    {
        if (_refusedKeyId == c.KeyId) return;
        _refusedKeyId = c.KeyId;
        queue.Clear();
        log?.Invoke("Encryption was reset; dropped the clips waiting to be sent.");
        try { KeyReset?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { }   // a throwing handler must not fail the copy or the flush
    }

    /// <summary>Nothing can be sealed right now: there is no key, or the server refused it.</summary>
    private sealed class VaultKeyUnavailableException() : Exception("No usable vault key.");
}
