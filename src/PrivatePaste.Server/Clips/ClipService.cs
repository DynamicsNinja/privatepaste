using System.Text.RegularExpressions;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Clips;

/// <summary>
/// The device that produced a clip. The web UI uses <c>(null, "Web", "web", userId)</c>. <see cref="Platform"/> is
/// lowercase, or null when unknown. <see cref="UserId"/> is the person signed in on it.
/// </summary>
public sealed record DeviceIdentity(Guid? Id, string Name, string? Platform, Guid? UserId);

public sealed class ClipRejectedException(int statusCode, string message, string? title = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>Problem title, when the default for the status isn't specific enough.</summary>
    public string? Title { get; } = title;
}

public static class ClipBlobs
{
    // Blobs are keyed by the clip's hash so a clip row is only visible once its blobs exist. They hold what the client
    // sent: ciphertext the server can't open.
    public static string Thumb(string hash) => $"{hash}.thumb";

    public static string Blob(string hash, int n) => $"{hash}.b{n}";
}

public sealed partial class ClipService(
    ClipRepository clips,
    SettingsRepository settings,
    VaultRepository vault,
    BlobStore blobs,
    IClipBroadcaster broadcaster,
    ServerOptions options,
    TimeProvider time,
    ILogger<ClipService> logger)
{
    /// <summary>The largest sealed thumbnail accepted.</summary>
    public const int MaxThumbBytes = 1024 * 1024;

    /// <summary>The largest sealed payload accepted on an image clip, which holds only its size.</summary>
    public const int MaxImagePayloadBytes = 4096;

    // Serializes an upload's key check, blob writes and upsert against blob deletion and the encryption reset, so an
    // upload sealed under the old key can't land after a reset (single-user server, so contention is negligible).
    private readonly SemaphoreSlim _storeLock = new(1, 1);

    /// <summary>
    /// The largest sealed text or link payload. The JSON escapes every non-ASCII character and some ASCII ones as
    /// <c>\uXXXX</c>, so a UTF-8 byte can take up to 6 bytes; 4096 more cover the JSON and the sealing. The limit the
    /// user sees is on the plaintext, through <see cref="ClipUploadMeta.SizeBytes"/>.
    /// </summary>
    public static long MaxTextPayloadBytesFor(ServerOptions options) => 6 * options.MaxTextBytes + 4096;

    /// <summary>The largest sealed image blob: the image limit plus the blob header and a tag per chunk.</summary>
    public static long MaxBlobBytesFor(ServerOptions options) =>
        options.MaxImageBytes + 9 + 16 * ((options.MaxImageBytes + BlobCipher.ChunkSize - 1) / BlobCipher.ChunkSize);

    /// <summary>The largest <c>meta</c> part: the base64 of the largest payload plus room for the other fields.</summary>
    public static long MaxMetaBytesFor(ServerOptions options) => Base64Length(MaxTextPayloadBytesFor(options)) + 8192;

    /// <summary>
    /// The largest upload body: an image at its limit with the largest thumbnail, or the largest text, each with room
    /// for the metadata and the multipart framing.
    /// </summary>
    public static long MaxRequestBodyBytesFor(ServerOptions options) =>
        Math.Max(MaxBlobBytesFor(options) + MaxThumbBytes + 64 * 1024,
            Base64Length(MaxTextPayloadBytesFor(options)) + 16 * 1024);

    private static long Base64Length(long bytes) => (bytes + 2) / 3 * 4;

    public long MaxTextPayloadBytes => MaxTextPayloadBytesFor(options);

    public long MaxBlobBytes => MaxBlobBytesFor(options);

    public long MaxMetaBytes => MaxMetaBytesFor(options);

    /// <summary>
    /// Stores a clip sealed by the client. The server checks only what it can see: the kind, the keyed hash, the key
    /// id and the sizes. A clip with the same hash is bumped instead of added again.
    /// </summary>
    public async Task<ClipDto> AddAsync(ClipUploadMeta meta, byte[]? thumb, IReadOnlyList<byte[]> blobParts, DeviceIdentity device)
    {
        var kind = meta.Kind switch
        {
            "text" => ClipKind.Text,
            "link" => ClipKind.Link,
            "image" => ClipKind.Image,
            _ => throw new ClipRejectedException(StatusCodes.Status400BadRequest, "Unknown clip kind."),
        };
        if (meta.Hash is null || !HashPattern().IsMatch(meta.Hash))
            throw new ClipRejectedException(StatusCodes.Status400BadRequest, "Invalid hash.");
        if (string.IsNullOrEmpty(meta.KeyId))
            throw new ClipRejectedException(StatusCodes.Status400BadRequest, "Missing key id.");
        if (meta.SizeBytes < 0)
            throw new ClipRejectedException(StatusCodes.Status400BadRequest, "Invalid size.");
        var payloadBytes = PayloadLength(meta.Payload);

        if (kind == ClipKind.Image)
        {
            if (blobParts.Count != 1)
                throw new ClipRejectedException(StatusCodes.Status400BadRequest, "An image clip has exactly one blob.");
            if (blobParts[0].Length > MaxBlobBytes)
                throw new ClipRejectedException(StatusCodes.Status413PayloadTooLarge,
                    $"Image exceeds the {options.MaxImageBytes} byte limit.");
            if (thumb is { Length: > MaxThumbBytes })
                throw new ClipRejectedException(StatusCodes.Status413PayloadTooLarge, "Thumbnail is too large.");
            if (payloadBytes > MaxImagePayloadBytes)
                throw new ClipRejectedException(StatusCodes.Status413PayloadTooLarge, "Payload is too large.");
        }
        else
        {
            if (blobParts.Count != 0 || thumb is not null)
                throw new ClipRejectedException(StatusCodes.Status400BadRequest, "A text clip has no blobs or thumbnail.");
            if (meta.SizeBytes > options.MaxTextBytes || payloadBytes > MaxTextPayloadBytes)
                throw new ClipRejectedException(StatusCodes.Status413PayloadTooLarge,
                    $"Text exceeds the {options.MaxTextBytes} byte limit.");
        }

        var copiedAt = CopiedAt(meta.CopiedAt, meta.Backfill);
        UpsertResult result;
        await _storeLock.WaitAsync();
        try
        {
            var current = await vault.GetAsync()
                ?? throw new ClipRejectedException(StatusCodes.Status409Conflict, "Set up encryption first.");
            if (current.KeyId != meta.KeyId)
                // Clients key off the title: they unlock with the new passphrase and drop what they had queued.
                throw new ClipRejectedException(StatusCodes.Status409Conflict,
                    "Encryption was reset. Enter the new passphrase.", "Encryption was reset");
            // Under the lock, so a backfill can't land between a clear recording its time and deleting.
            if (meta.Backfill)
                await EnsureNotClearedAsync(copiedAt);
            // An image and a text clip under one hash would pair one clip's row with another's blobs. Text and link
            // share a shape (no blobs, no thumb) and platforms can disagree on whether some content is a link, so
            // that clash is a bump that keeps the stored kind.
            if (await clips.GetByHashAsync(meta.Hash) is { } existing
                && (existing.Kind == ClipKind.Image) != (kind == ClipKind.Image))
                throw new ClipRejectedException(StatusCodes.Status409Conflict,
                    "That content was already stored as a different kind.");

            // Always (re)write: the upload carries the bytes anyway, and this repairs a blob that is missing or
            // damaged instead of trusting that it exists. Each part is sealed on its own, so new ones sit fine beside
            // a bumped clip's stored payload.
            if (thumb is not null)
                await blobs.WriteAsync(ClipBlobs.Thumb(meta.Hash), thumb);
            for (var n = 0; n < blobParts.Count; n++)
                await blobs.WriteAsync(ClipBlobs.Blob(meta.Hash, n), blobParts[n]);

            result = await clips.UpsertAsync(new NewClip(kind, null, meta.Hash, null, null, meta.SizeBytes, device.Id,
                device.Name, device.Platform, copiedAt, device.UserId, meta.Payload, meta.KeyId, thumb is not null,
                blobParts.Count));
        }
        finally
        {
            _storeLock.Release();
        }

        await broadcaster.ClipChangedAsync(result.Clip, live: !meta.Backfill);
        return result.Clip;
    }

    /// <summary>The decoded size of a base64 payload; 400 when it is missing or not base64.</summary>
    private static int PayloadLength(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            throw new ClipRejectedException(StatusCodes.Status400BadRequest, "Missing payload.");
        var buffer = new byte[payload.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(payload, buffer, out var length))
            throw new ClipRejectedException(StatusCodes.Status400BadRequest, "Payload is not base64.");
        return length;
    }

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex HashPattern();

    public async Task<ClipDto?> RestoreAsync(Guid id, DeviceIdentity device)
    {
        var clip = await clips.BumpAsync(id, device.Id, device.Name, device.Platform, device.UserId, time.GetUtcNow());
        if (clip is null)
            return null;
        await broadcaster.ClipChangedAsync(clip, live: true);
        return clip;
    }

    /// <summary>Pins or unpins a clip. Null if it doesn't exist; only a real change is broadcast.</summary>
    public async Task<ClipDto?> SetPinnedAsync(Guid id, bool pinned)
    {
        var before = await clips.GetAsync(id);
        if (before is null)
            return null;
        var clip = await clips.SetPinnedAsync(id, pinned, time.GetUtcNow());
        if (clip is not null && clip.Seq != before.Seq)
            await broadcaster.ClipChangedAsync(clip, live: false);
        return clip;
    }

    /// <summary>
    /// Re-sends a person's clips (every clip when <paramref name="userId"/> is null) with new seqs, so devices pick up
    /// a changed name. Like a pin, they go out as non-live changes and keep their copy time, so no device pastes them.
    /// A socket whose outbox overflows is dropped and catches up by seq when it reconnects.
    /// </summary>
    public async Task ResendAsync(Guid? userId)
    {
        foreach (var clip in await clips.ResequenceAsync(userId))
            await broadcaster.ClipChangedAsync(clip, live: false);
    }

    /// <summary>
    /// Deletes every unpinned clip copied (or unpinned) before now and remembers when, so a device that was offline can't
    /// bring older clips back with a backfill upload. Returns how many clips were deleted.
    /// </summary>
    public async Task<int> ClearAsync()
    {
        // Recorded first so a backfill racing with the delete is already refused; the same time is the delete's cutoff,
        // so a live upload landing between the two is kept.
        var at = time.GetUtcNow();
        await settings.SetHistoryClearedAtAsync(at);
        return await DeleteUnpinnedAsync(at);
    }

    /// <summary>
    /// Stores the new vault wrap and deletes everything, atomically (see <see cref="ClipRepository.ResetAllAsync"/>),
    /// then the blobs, best effort. Used when the owner resets encryption, after which no old clip could be
    /// opened anyway. The caller announces it with <see cref="IClipBroadcaster.VaultResetAsync"/>, so no per-clip
    /// deletes are sent.
    /// </summary>
    public async Task ResetAllAsync(VaultWrapDto wrap)
    {
        await _storeLock.WaitAsync();
        try
        {
            foreach (var clip in await clips.ResetAllAsync(wrap))
                RemoveBlobs(clip);
        }
        finally
        {
            _storeLock.Release();
        }
    }

    /// <summary>Deletes unpinned clips last copied before <paramref name="cutoff"/> (the auto-delete sweep).</summary>
    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff) => DeleteUnpinnedAsync(cutoff);

    private async Task<int> DeleteUnpinnedAsync(DateTimeOffset? olderThan)
    {
        IReadOnlyList<(ClipDto Clip, long Seq)> deleted;
        await _storeLock.WaitAsync();
        try
        {
            deleted = await clips.DeleteUnpinnedAsync(olderThan);
            foreach (var (clip, _) in deleted)
                RemoveBlobs(clip);
        }
        finally
        {
            _storeLock.Release();
        }

        foreach (var (clip, seq) in deleted)
            await broadcaster.ClipDeletedAsync(clip.Id, seq);
        return deleted.Count;
    }

    /// <summary>
    /// Refuses a backfilled clip that was copied before the history was cleared or is older than the auto-delete age:
    /// it would otherwise come back on the next sync from a device that was offline.
    /// </summary>
    private async Task EnsureNotClearedAsync(DateTimeOffset copiedAt)
    {
        var clearedAt = await settings.GetHistoryClearedAtAsync();
        var days = await settings.GetAutoDeleteDaysAsync();
        if ((clearedAt is { } c && copiedAt < c) || (days is { } d && copiedAt < time.GetUtcNow().AddDays(-d)))
            // Android 0.1.2 shows the detail as its notification text.
            throw new ClipRejectedException(StatusCodes.Status410Gone,
                "History was cleared after this clip was copied, so it wasn't added.", "Cleared");
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        long seq;
        await _storeLock.WaitAsync();
        try
        {
            var clip = await clips.GetAsync(id);
            if (clip is null || await clips.DeleteAsync(id) is not { } deletedSeq)
                return false;
            seq = deletedSeq;
            RemoveBlobs(clip);
        }
        finally
        {
            _storeLock.Release();
        }

        await broadcaster.ClipDeletedAsync(id, seq);
        return true;
    }

    // The row is gone, so the delete has happened: a blob that can't be removed now is only an orphaned
    // (still encrypted) file, and must not stop the deletion from being reported to the other devices.
    // Blobs are keyed by content hash and a hash has exactly one clip row, so no surviving clip shares them.
    private void RemoveBlobs(ClipDto clip)
    {
        if (clip.HasThumb)
            TryDeleteBlob(ClipBlobs.Thumb(clip.Hash));
        for (var n = 0; n < clip.BlobCount; n++)
            TryDeleteBlob(ClipBlobs.Blob(clip.Hash, n));
    }

    private void TryDeleteBlob(string name)
    {
        try
        {
            blobs.Delete(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete blob {Blob}", name);
        }
    }

    private DateTimeOffset CopiedAt(DateTimeOffset? requested, bool backfill)
    {
        var now = time.GetUtcNow();
        if (!backfill) return now;
        var at = requested ?? now;
        return at < now ? at : now;
    }
}
