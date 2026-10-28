using System.Globalization;
using System.Security.Claims;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Api;

public static class ClipEndpoints
{
    public static void MapClipEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization(AuthSchemes.OwnerPolicy);
        group.AddEndpointFilter(async (ctx, next) =>
        {
            try
            {
                return await next(ctx);
            }
            catch (ClipRejectedException ex)
            {
                return Results.Problem(statusCode: ex.StatusCode, detail: ex.Message);
            }
        });

        group.MapPost("clips/text", (TextClipUpload upload, ClaimsPrincipal user, ClipService service) =>
            service.AddTextAsync(upload, user.ToDeviceIdentity()));

        group.MapPost("clips/image", async (HttpRequest request, ClaimsPrincipal user, ClipService service) =>
        {
            if (!request.HasFormContentType)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Expected multipart form data.");

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Missing 'file' part.");

            DateTimeOffset? copiedAt = null;
            if (form.TryGetValue("copiedAt", out var rawAt) && rawAt.Count > 0 && rawAt[0] is { Length: > 0 } atText)
            {
                if (!DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid copiedAt.");
                copiedAt = parsed;
            }

            var backfill = form.TryGetValue("backfill", out var rawBackfill) && bool.TryParse(rawBackfill.ToString(), out var b) && b;

            await using var stream = file.OpenReadStream();
            return Results.Ok(await service.AddImageAsync(stream, copiedAt, backfill, user.ToDeviceIdentity()));
        });

        group.MapGet("clips", async (ClipRepository repo, string? q, string? cursor, int? limit) =>
        {
            try
            {
                return Results.Ok(await repo.ListAsync(q, cursor, limit ?? 50));
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid cursor.");
            }
        });

        group.MapGet("clips/{id:guid}", async (Guid id, ClipRepository repo) =>
            await repo.GetAsync(id) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapGet("clips/{id:guid}/image", (Guid id, ClipRepository repo, BlobStore blobs) =>
            ServeBlobAsync(id, ClipBlobs.Image(id), repo, blobs));

        group.MapGet("clips/{id:guid}/thumbnail", (Guid id, ClipRepository repo, BlobStore blobs) =>
            ServeBlobAsync(id, ClipBlobs.Thumbnail(id), repo, blobs));

        group.MapPost("clips/{id:guid}/restore", async (Guid id, ClaimsPrincipal user, ClipService service) =>
            await service.RestoreAsync(id, user.ToDeviceIdentity()) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapDelete("clips/{id:guid}", async (Guid id, ClipService service) =>
            await service.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        group.MapGet("sync", async (ClipRepository repo, long? afterSeq, int? limit) =>
            Results.Ok(await repo.SyncAsync(afterSeq ?? 0, limit ?? 200)));
    }

    private static async Task<IResult> ServeBlobAsync(Guid id, string blobName, ClipRepository repo, BlobStore blobs)
    {
        var clip = await repo.GetAsync(id);
        if (clip is null || clip.Kind != ClipKind.Image)
            return Results.NotFound();
        var bytes = await blobs.ReadAsync(blobName);
        return bytes is null ? Results.NotFound() : Results.File(bytes, "image/png");
    }
}
