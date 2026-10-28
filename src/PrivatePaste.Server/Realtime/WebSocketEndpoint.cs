using System.Security.Claims;
using PrivatePaste.Server.Auth;

namespace PrivatePaste.Server.Realtime;

public static class WebSocketEndpoint
{
    public static void MapWebSocketEndpoint(this IEndpointRouteBuilder app)
    {
        // Native clients send the bearer token in the upgrade request header, so only the device-token scheme applies.
        app.Map("/ws", async (HttpContext context, ClaimsPrincipal user, ConnectionHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest || user.DeviceId() is not { } deviceId)
                return Results.StatusCode(StatusCodes.Status400BadRequest);

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(deviceId, socket, context.RequestAborted);
            return Results.Empty;
        }).RequireAuthorization(p => p
            .AddAuthenticationSchemes(AuthSchemes.DeviceToken)
            .RequireAuthenticatedUser());
    }
}
