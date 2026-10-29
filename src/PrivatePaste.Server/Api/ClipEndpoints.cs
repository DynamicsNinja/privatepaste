using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Api;

public static class ClipEndpoints
{
    public static void MapClipEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization(AuthSchemes.OwnerPolicy).RequireAntiforgeryForCookieAuth();
        group.AddEndpointFilter(async (ctx, next) =>
        {
            try
            {
                return await next(ctx);
            }
            catch (ClipRejectedException ex)
            {
                return Results.Problem(statusCode: ex.StatusCode, detail: ex.Message, title: ex.Title);
            }
        });

        group.MapPost("clips/text", (TextClipUpload upload, ClaimsPrincipal user, ClipService service) =>
            service.AddTextAsync(upload, user.ToDeviceIdentity()));

        // Parses the multipart body by hand so the image is read into memory (bounded by MaxImageBytes) and never
        // buffered to disk. Parts may come in any order: the image is only processed once every part has been read.
        group.MapPost("clips/image", async (HttpRequest request, ClaimsPrincipal user, ClipService service) =>
        {
            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
                || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
                || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 } boundary)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Expected multipart form data.");

            var reader = new MultipartReader(boundary, request.Body);
            DateTimeOffset? copiedAt = null;
            var backfill = false;
            MemoryStream? image = null;
            try
            {
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync()) is not null)
                {
                    var disposition = section.GetContentDispositionHeader();
                    if (disposition is null || !(disposition.IsFormDisposition() || disposition.IsFileDisposition()))
                        continue;
                    var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;

                    if (name == "file")
                    {
                        if (image is not null)
                            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Duplicate 'file' part.");
                        image = await service.BufferImageAsync(section.Body);
                    }
                    else if (name is "copiedAt" or "backfill")
                    {
                        using var text = new StreamReader(section.Body, Encoding.UTF8, leaveOpen: true);
                        var value = (await ReadSmallAsync(text)).Trim();
                        if (name == "backfill")
                            backfill = bool.TryParse(value, out var b) && b;
                        else if (value.Length > 0)
                        {
                            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid copiedAt.");
                            copiedAt = parsed;
                        }
                    }
                }

                if (image is null)
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Missing 'file' part.");
                return Results.Ok(await service.AddImageAsync(image, copiedAt, backfill, user.ToDeviceIdentity()));
            }
            catch (InvalidDataException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Malformed multipart body.");
            }
            finally
            {
                image?.Dispose();
            }
        });

        group.MapGet("clips", async (ClipRepository repo, string? q, string? cursor, int? limit, bool? pinned) =>
        {
            try
            {
                return Results.Ok(pinned == true
                    ? await repo.ListPinnedAsync(q, cursor, limit ?? 50)
                    : await repo.ListAsync(q, cursor, limit ?? 50));
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid cursor.");
            }
        });

        group.MapGet("clips/{id:guid}", async (Guid id, ClipRepository repo) =>
            await repo.GetAsync(id) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapGet("clips/{id:guid}/image", (Guid id, ClipRepository repo, BlobStore blobs) =>
            ServeBlobAsync(id, ClipBlobs.Image, repo, blobs));

        group.MapGet("clips/{id:guid}/thumbnail", (Guid id, ClipRepository repo, BlobStore blobs) =>
            ServeBlobAsync(id, ClipBlobs.Thumbnail, repo, blobs));

        group.MapPost("clips/{id:guid}/restore", async (Guid id, ClaimsPrincipal user, ClipService service) =>
            await service.RestoreAsync(id, user.ToDeviceIdentity()) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapPost("clips/{id:guid}/pin", async (Guid id, ClipService service) =>
            await service.SetPinnedAsync(id, true) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapDelete("clips/{id:guid}/pin", async (Guid id, ClipService service) =>
            await service.SetPinnedAsync(id, false) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapPost("history/clear", async (ClipService service) =>
            Results.Ok(new { deleted = await service.ClearAsync() }));

        group.MapDelete("clips/{id:guid}", async (Guid id, ClipService service) =>
            await service.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        group.MapGet("sync", async (ClipRepository repo, long? afterSeq, int? limit) =>
            Results.Ok(await repo.SyncAsync(afterSeq ?? 0, limit ?? 200)));
    }

    private static async Task<string> ReadSmallAsync(StreamReader reader)
    {
        var buffer = new char[256];
        var n = await reader.ReadBlockAsync(buffer);
        return new string(buffer, 0, n);
    }

    private static async Task<IResult> ServeBlobAsync(Guid id, Func<string, string> blobName, ClipRepository repo, BlobStore blobs)
    {
        var clip = await repo.GetAsync(id);
        if (clip is null || clip.Kind != ClipKind.Image)
            return Results.NotFound();
        var bytes = await blobs.ReadAsync(blobName(clip.Hash));
        return bytes is null ? Results.NotFound() : Results.File(bytes, "image/png");
    }
}
