using System.Text;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Images;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Clips;

/// <summary>
/// The device that produced a clip. The web UI uses <c>(null, "Web", "web")</c>. <see cref="Platform"/> is lowercase,
/// or null when unknown.
/// </summary>
public sealed record DeviceIdentity(Guid? Id, string Name, string? Platform);

public sealed class ClipRejectedException(int statusCode, string message, string? title = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>Problem title, when the default for the status isn't specific enough.</summary>
    public string? Title { get; } = title;
}

public static class ClipBlobs
{
    /// <summary>Blobs are keyed by content hash so a clip row is only visible once its blobs exist.</summary>
    public static string Image(string hash) => $"{hash}.png";

    public static string Thumbnail(string hash) => $"{hash}.thumb.png";
}

public sealed class ClipService(
    ClipRepository clips,
    SettingsRepository settings,
    BlobStore blobs,
    ImageProcessor images,
    IClipBroadcaster broadcaster,
    ServerOptions options,
    TimeProvider time,
    ILogger<ClipService> logger)
{
    // Serializes blob-write + upsert and blob deletion (single-user server, so contention is negligible).
    private readonly SemaphoreSlim _imageLock = new(1, 1);

    public async Task<ClipDto> AddTextAsync(TextClipUpload upload, DeviceIdentity device)
    {
        if (string.IsNullOrWhiteSpace(upload.Text))
            throw new ClipRejectedException(StatusCodes.Status400BadRequest, "Text is empty.");

        var text = ContentHash.NormalizeText(upload.Text);
        var size = Encoding.UTF8.GetByteCount(text);
        if (size > options.MaxTextBytes)
            throw new ClipRejectedException(StatusCodes.Status413PayloadTooLarge,
                $"Text exceeds the {options.MaxTextBytes} byte limit.");

        var copiedAt = CopiedAt(upload.CopiedAt, upload.Backfill);
        if (upload.Backfill)
            await EnsureNotClearedAsync(copiedAt);

        var result = await clips.UpsertAsync(new NewClip(ContentClassifier.ClassifyText(text), text,
            ContentHash.ForText(text), null, null, size, device.Id, device.Name, device.Platform, copiedAt));
        await broadcaster.ClipChangedAsync(result.Clip, live: !upload.Backfill);
        return result.Clip;
    }

    /// <summary>Adds an image already read into memory with <see cref="BufferImageAsync"/>.</summary>
    public async Task<ClipDto> AddImageAsync(MemoryStream image, DateTimeOffset? copiedAt, bool backfill, DeviceIdentity device)
    {
        ProcessedImage processed;
        try
        {
            image.Position = 0;
            processed = images.Process(image);
        }
        catch (ImageTooLargeException ex)
        {
            throw new ClipRejectedException(StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (InvalidImageException ex)
        {
            throw new ClipRejectedException(StatusCodes.Status400BadRequest, ex.Message);
        }

        var effectiveCopiedAt = CopiedAt(copiedAt, backfill);
        if (backfill)
            await EnsureNotClearedAsync(effectiveCopiedAt);

        UpsertResult result;
        await _imageLock.WaitAsync();
        try
        {
            // Always (re)write: the upload carries the bytes anyway, and this repairs a blob that is missing or
            // damaged (e.g. left truncated by a crash before writes were atomic) instead of trusting that it exists.
            await blobs.WriteAsync(ClipBlobs.Image(processed.Hash), processed.Png);
            await blobs.WriteAsync(ClipBlobs.Thumbnail(processed.Hash), processed.ThumbnailPng);

            result = await clips.UpsertAsync(new NewClip(ClipKind.Image, null, processed.Hash, processed.Width,
                processed.Height, processed.Png.Length, device.Id, device.Name, device.Platform, effectiveCopiedAt));
        }
        finally
        {
            _imageLock.Release();
        }

        await broadcaster.ClipChangedAsync(result.Clip, live: !backfill);
        return result.Clip;
    }

    public async Task<ClipDto?> RestoreAsync(Guid id, DeviceIdentity device)
    {
        var clip = await clips.BumpAsync(id, device.Id, device.Name, device.Platform, time.GetUtcNow());
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
    /// Deletes every unpinned clip and remembers when, so a device that was offline can't bring older clips back
    /// with a backfill upload. Returns how many clips were deleted.
    /// </summary>
    public async Task<int> ClearAsync()
    {
        // Recorded first so a backfill racing with the delete is already refused.
        await settings.SetHistoryClearedAtAsync(time.GetUtcNow());
        return await DeleteUnpinnedAsync(null);
    }

    /// <summary>Deletes unpinned clips last copied before <paramref name="cutoff"/> (the auto-delete sweep).</summary>
    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff) => DeleteUnpinnedAsync(cutoff);

    private async Task<int> DeleteUnpinnedAsync(DateTimeOffset? olderThan)
    {
        IReadOnlyList<(ClipDto Clip, long Seq)> deleted;
        await _imageLock.WaitAsync();
        try
        {
            deleted = await clips.DeleteUnpinnedAsync(olderThan);
            foreach (var (clip, _) in deleted)
                RemoveBlobs(clip);
        }
        finally
        {
            _imageLock.Release();
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
            throw new ClipRejectedException(StatusCodes.Status410Gone, "Cleared", "Cleared");
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        long seq;
        await _imageLock.WaitAsync();
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
            _imageLock.Release();
        }

        await broadcaster.ClipDeletedAsync(id, seq);
        return true;
    }

    // The row is gone, so the delete has happened: a blob that can't be removed now is only an orphaned
    // (still encrypted) file, and must not stop the deletion from being reported to the other devices.
    // Blobs are keyed by content hash and a hash has exactly one clip row, so no surviving clip shares them.
    private void RemoveBlobs(ClipDto clip)
    {
        if (clip.Kind != ClipKind.Image) return;
        TryDeleteBlob(ClipBlobs.Image(clip.Hash));
        TryDeleteBlob(ClipBlobs.Thumbnail(clip.Hash));
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

    /// <summary>
    /// Reads an uploaded image into memory (never to disk), rejecting it with 413 as soon as it exceeds
    /// <see cref="ServerOptions.MaxImageBytes"/>.
    /// </summary>
    public async Task<MemoryStream> BufferImageAsync(Stream input)
    {
        var buffer = new MemoryStream();
        try
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > options.MaxImageBytes)
                    throw new ClipRejectedException(StatusCodes.Status413PayloadTooLarge,
                        $"Image exceeds the {options.MaxImageBytes} byte limit.");
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
