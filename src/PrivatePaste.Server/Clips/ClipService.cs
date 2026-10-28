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
    public static string Image(Guid id) => $"{id}.png";

    public static string Thumbnail(Guid id) => $"{id}.thumb.png";
}

public sealed class ClipService(
    ClipRepository clips,
    BlobStore blobs,
    ImageProcessor images,
    IClipBroadcaster broadcaster,
    ServerOptions options,
    TimeProvider time)
{
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

    public async Task<ClipDto> AddImageAsync(Stream image, DateTimeOffset? copiedAt, bool backfill, DeviceIdentity device)
    {
        using var buffered = await ReadLimitedAsync(image);

        ProcessedImage processed;
        try
        {
            processed = images.Process(buffered);
        }
        catch (InvalidImageException ex)
        {
            throw new ClipRejectedException(StatusCodes.Status400BadRequest, ex.Message);
        }

        var result = await clips.UpsertAsync(new NewClip(ClipKind.Image, null, processed.Hash, processed.Width,
            processed.Height, processed.Png.Length, device.Id, device.Name, CopiedAt(copiedAt, backfill)));
        if (result.Created)
        {
            await blobs.WriteAsync(ClipBlobs.Image(result.Clip.Id), processed.Png);
            await blobs.WriteAsync(ClipBlobs.Thumbnail(result.Clip.Id), processed.ThumbnailPng);
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
        var seq = await clips.DeleteAsync(id);
        if (seq is null)
            return false;
        blobs.Delete(ClipBlobs.Image(id));
        blobs.Delete(ClipBlobs.Thumbnail(id));
        await broadcaster.ClipDeletedAsync(id, seq.Value);
        return true;
    }

    private DateTimeOffset CopiedAt(DateTimeOffset? requested, bool backfill)
    {
        var now = time.GetUtcNow();
        if (!backfill) return now;
        var at = requested ?? now;
        return at < now ? at : now;
    }

    private async Task<MemoryStream> ReadLimitedAsync(Stream input)
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
