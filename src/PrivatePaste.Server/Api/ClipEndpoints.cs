using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Api;

public static partial class ClipEndpoints
{
    public static void MapClipEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization(AuthSchemes.SignedInPolicy).RequireAntiforgeryForCookieAuth();
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

        // Parses the multipart body by hand so every part is read into memory, each bounded by its own limit, and never
        // buffered to disk. Parts may come in any order: the clip is only checked and stored once every part is read.
        group.MapPost("clips", async (HttpRequest request, ClaimsPrincipal user, ClipService service) =>
        {
            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
                || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
                || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 } boundary)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Expected multipart form data.");

            var reader = new MultipartReader(boundary, request.Body);
            byte[]? meta = null;
            byte[]? thumb = null;
            var blobs = new SortedDictionary<int, byte[]>();
            try
            {
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync()) is not null)
                {
                    var disposition = section.GetContentDispositionHeader();
                    if (disposition is null || !(disposition.IsFormDisposition() || disposition.IsFileDisposition()))
                        continue;
                    var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;

                    if (name == "meta")
                    {
                        if (meta is not null)
                            return Duplicate(name);
                        meta = await ReadBoundedAsync(section.Body, service.MaxMetaBytes, "Clip metadata");
                    }
                    else if (name == "thumb")
                    {
                        if (thumb is not null)
                            return Duplicate(name);
                        thumb = await ReadBoundedAsync(section.Body, ClipService.MaxThumbBytes, "Thumbnail");
                    }
                    else if (name is not null && BlobPart().Match(name) is { Success: true } match)
                    {
                        // The pattern allows at most four digits, so this always fits.
                        var n = int.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
                        if (blobs.ContainsKey(n))
                            return Duplicate(name);
                        blobs[n] = await ReadBoundedAsync(section.Body, service.MaxBlobBytes, "Blob");
                    }
                }

                if (meta is null)
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Missing 'meta' part.");
                ClipUploadMeta? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<ClipUploadMeta>(meta, ProtocolJson.Options);
                }
                catch (JsonException)
                {
                    parsed = null;
                }
                if (parsed is null)
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid 'meta' part.");
                // blob0, blob1, ... with no gaps.
                if (blobs.Count > 0 && blobs.Keys.Last() != blobs.Count - 1)
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Blob parts must be numbered from 0 without gaps.");

                return Results.Ok(await service.AddAsync(parsed, thumb, blobs.Values.ToList(), user.ToDeviceIdentity()));
            }
            catch (InvalidDataException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Malformed multipart body.");
            }
        });

        group.MapGet("clips", async (ClipRepository repo, string? cursor, int? limit, bool? pinned, string? q) =>
        {
            // The server only holds ciphertext, so it can't search; an old client asking would get a wrong, empty answer.
            if (q is not null)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Search runs on your devices");
            try
            {
                return Results.Ok(pinned == true
                    ? await repo.ListPinnedAsync(cursor, limit ?? 50)
                    : await repo.ListAsync(cursor, limit ?? 50));
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid cursor.");
            }
        });

        group.MapGet("clips/{id:guid}", async (Guid id, ClipRepository repo) =>
            await repo.GetAsync(id) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapGet("clips/{id:guid}/thumb", async (Guid id, ClipRepository repo, BlobStore blobs) =>
            await repo.GetAsync(id) is { HasThumb: true } clip
                ? await ServeBlobAsync(ClipBlobs.Thumb(clip.Hash), blobs)
                : Results.NotFound());

        group.MapGet("clips/{id:guid}/blobs/{n:int}", async (Guid id, int n, ClipRepository repo, BlobStore blobs) =>
            await repo.GetAsync(id) is { } clip && n >= 0 && n < clip.BlobCount
                ? await ServeBlobAsync(ClipBlobs.Blob(clip.Hash, n), blobs)
                : Results.NotFound());

        group.MapPost("clips/{id:guid}/restore", async (Guid id, ClaimsPrincipal user, ClipService service) =>
            await service.RestoreAsync(id, user.ToDeviceIdentity()) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapPost("clips/{id:guid}/pin", async (Guid id, ClipService service) =>
            await service.SetPinnedAsync(id, true) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapDelete("clips/{id:guid}/pin", async (Guid id, ClipService service) =>
            await service.SetPinnedAsync(id, false) is { } clip ? Results.Ok(clip) : Results.NotFound());

        group.MapPost("history/clear", async (ClipService service) =>
            Results.Ok(new { deleted = await service.ClearAsync() })).RequireAuthorization(AuthSchemes.OwnerPolicy);

        group.MapDelete("clips/{id:guid}", async (Guid id, ClipService service) =>
            await service.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        group.MapGet("sync", async (ClipRepository repo, long? afterSeq, int? limit) =>
            Results.Ok(await repo.SyncAsync(afterSeq ?? 0, limit ?? 200)));
    }

    private static IResult Duplicate(string name) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"Duplicate '{name}' part.");

    /// <summary>Reads a part into memory, answering 413 as soon as it exceeds <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream input, long limit, string what)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new ClipRejectedException(StatusCodes.Status413PayloadTooLarge, $"{what} exceeds the {limit} byte limit.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // What the server stores is the client's ciphertext, served as is.
    private static async Task<IResult> ServeBlobAsync(string name, BlobStore blobs) =>
        await blobs.ReadAsync(name) is { } bytes ? Results.File(bytes, "application/octet-stream") : Results.NotFound();

    [GeneratedRegex(@"^blob([0-9]{1,4})\z")]
    private static partial Regex BlobPart();
}
