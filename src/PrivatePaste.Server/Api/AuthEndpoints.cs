using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Realtime;

namespace PrivatePaste.Server.Api;

public static class AuthEndpoints
{
    private const int MaxDeviceNameLength = 50;

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest request, PasswordVerifier verifier, DeviceRepository devices) =>
        {
            if (!verifier.Verify(request.Password))
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, detail: "Invalid password.");

            var name = request.DeviceName?.Trim() ?? "";
            if (name.Length == 0 || name.Length > MaxDeviceNameLength)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    detail: $"DeviceName must be 1-{MaxDeviceNameLength} characters.");

            var platform = request.Platform?.Trim() ?? "";
            if (platform.Length == 0) platform = "unknown";
            if (platform.Length > MaxDeviceNameLength) platform = platform[..MaxDeviceNameLength];

            var (device, token) = await devices.CreateAsync(name, platform);
            return Results.Ok(new LoginResponse(device.Id, token));
        }).RequireRateLimiting("login").AllowAnonymous();

        app.MapPost("/api/auth/logout", async (HttpContext http, ClaimsPrincipal user, DeviceRepository devices, ConnectionHub hub) =>
        {
            if (user.DeviceId() is { } id)
            {
                await devices.RevokeAsync(id);
                await hub.CloseDeviceAsync(id);
            }
            else
            {
                // A web session (cookie principal) has no device to revoke; end the session itself.
                await http.SignOutAsync(AuthSchemes.Cookies);
            }
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.OwnerPolicy).RequireAntiforgeryForCookieAuth();
    }
}
