using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Config;

namespace PrivatePaste.Server.Api;

public static class ServerInfoEndpoints
{
    public const int ProtocolVersion = 2;

    public static void MapServerInfoEndpoints(this IEndpointRouteBuilder app)
    {
        // Clients read this before signing in to learn what the server supports.
        app.MapGet("/api/server-info", (ServerOptions options) => Results.Ok(new ServerInfoDto(
            ProtocolVersion,
            ["text", "link", "image", "file"],
            new ServerLimitsDto((int)(options.MaxTextBytes / 1024), (int)(options.MaxImageBytes / (1024 * 1024)),
                (int)(options.MaxFileBytes / (1024 * 1024))))))
            .AllowAnonymous();

        app.MapGet("/api/me", async (System.Security.Claims.ClaimsPrincipal user, UserRepository users) =>
        {
            var person = user.UserId() is { } id ? await users.GetAsync(id) : await users.GetOwnerAsync();
            return person is null ? Results.Unauthorized() : Results.Ok(new MeDto(person.Name, person.IsOwner));
        }).RequireAuthorization(AuthSchemes.SignedInPolicy);
    }
}
