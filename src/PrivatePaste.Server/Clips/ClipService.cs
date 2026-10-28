using System.Text;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Images;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Clips;

/// <summary>The device that produced a clip. The web UI uses <c>(null, "Web")</c>.</summary>
public sealed record DeviceIdentity(Guid? Id, string Name);

public sealed class ClipRejectedException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class ClipBlobs
{
    /// <summary>Blobs are keyed by content hash so a clip row is only visible once its blobs exist.</summary>
    public static string Image(string hash) => $"{hash}.png";

    public static string Thumbnail(string hash) => $"{hash}.thumb.png";
}

public sealed class ClipService(
    ClipRepository clips,
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

        var result = await clips.UpsertAsync(new NewClip(ContentClassifier.ClassifyText(text), text,
            ContentHash.ForText(text), null, null, size, device.Id, device.Name, CopiedAt(upload.CopiedAt, upload.Backfill)));
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
        catch (InvalidImageException ex)
        {
            throw new ClipRejectedException(StatusCodes.Status400BadRequest, ex.Message);
        }

        UpsertResult result;
        await _imageLock.WaitAsync();
        try
        {
            // Always (re)write: the upload carries the bytes anyway, and this repairs a blob that is missing or
            // damaged (e.g. left truncated by a crash before writes were atomic) instead of trusting that it exists.
            await blobs.WriteAsync(ClipBlobs.Image(processed.Hash), processed.Png);
            await blobs.WriteAsync(ClipBlobs.Thumbnail(processed.Hash), processed.ThumbnailPng);

            result = await clips.UpsertAsync(new NewClip(ClipKind.Image, null, processed.Hash, processed.Width,
                processed.Height, processed.Png.Length, device.Id, device.Name, CopiedAt(copiedAt, backfill)));
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
        var clip = await clips.BumpAsync(id, device.Id, device.Name, time.GetUtcNow());
        if (clip is null)
            return null;
        await broadcaster.ClipChangedAsync(clip, live: true);
        return clip;
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
            if (clip.Kind == ClipKind.Image)
            {
                // The row is gone, so the delete has happened: a blob that can't be removed now is only an orphaned
                // (still encrypted) file, and must not stop the deletion from being reported to the other devices.
                TryDeleteBlob(ClipBlobs.Image(clip.Hash));
                TryDeleteBlob(ClipBlobs.Thumbnail(clip.Hash));
            }
        }
        finally
        {
            _imageLock.Release();
        }

        await broadcaster.ClipDeletedAsync(id, seq);
        return true;
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
