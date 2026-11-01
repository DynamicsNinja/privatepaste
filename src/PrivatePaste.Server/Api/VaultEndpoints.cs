using System.Text.RegularExpressions;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Api;

public static partial class VaultEndpoints
{
    // The clients' own bounds: weaker settings are refused, and so are settings so heavy they'd stall a phone.
    private const int MinMemoryKiB = 65536, MaxMemoryKiB = 262144;
    private const int MinIterations = 3, MaxIterations = 10;
    private const int MinParallelism = 1, MaxParallelism = 8;

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex KeyIdPattern();

    public static void MapVaultEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vault").RequireAuthorization(AuthSchemes.SignedInPolicy).RequireAntiforgeryForCookieAuth();

        group.MapGet("", async (VaultRepository vault) =>
            await vault.GetAsync() is { } wrap ? Results.Ok(wrap) : Results.NotFound());

        group.MapPut("", async (VaultWrapDto body, VaultRepository vault) =>
        {
            if (Validate(body) is { } error)
                return BadRequest(error);
            if (await vault.CreateAsync(body))
                return Results.Created("/api/vault", body);
            if (await vault.ReplaceWrapAsync(body))
                return Results.Ok(body);
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: "Encryption is already set up with a different key.");
        }).RequireAuthorization(AuthSchemes.OwnerPolicy);

        group.MapPost("reset", async (VaultWrapDto body, VaultRepository vault, ClipService clips, IClipBroadcaster broadcaster) =>
        {
            if (Validate(body) is { } error)
                return BadRequest(error);
            await clips.ResetAllAsync();
            await vault.ReplaceAllAsync(body);
            await broadcaster.VaultResetAsync();
            return Results.Ok(body);
        }).RequireAuthorization(AuthSchemes.OwnerPolicy);
    }

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: detail);

    private static string? Validate(VaultWrapDto? wrap)
    {
        if (wrap?.Kdf is null || wrap.KeyId is null || wrap.Nonce is null || wrap.WrappedKey is null || wrap.Kdf.Salt is null)
            return "The vault wrap is incomplete.";
        if (!KeyIdPattern().IsMatch(wrap.KeyId))
            return "keyId must be 16 lowercase hex characters.";
        var kdf = wrap.Kdf;
        if (kdf.Alg != "argon2id")
            return "kdf.alg must be argon2id.";
        if (kdf.MemoryKiB is < MinMemoryKiB or > MaxMemoryKiB)
            return $"kdf.memoryKiB must be {MinMemoryKiB} to {MaxMemoryKiB}.";
        if (kdf.Iterations is < MinIterations or > MaxIterations)
            return $"kdf.iterations must be {MinIterations} to {MaxIterations}.";
        if (kdf.Parallelism is < MinParallelism or > MaxParallelism)
            return $"kdf.parallelism must be {MinParallelism} to {MaxParallelism}.";
        if (!HasLength(kdf.Salt, 16))
            return "kdf.salt must be 16 bytes.";
        if (!HasLength(wrap.Nonce, 12))
            return "nonce must be 12 bytes.";
        if (!HasLength(wrap.WrappedKey, 48))
            return "wrappedKey must be 48 bytes.";
        return null;
    }

    private static bool HasLength(string base64, int bytes)
    {
        Span<byte> buffer = stackalloc byte[bytes + 3];
        return Convert.TryFromBase64String(base64, buffer, out var written) && written == bytes;
    }
}
