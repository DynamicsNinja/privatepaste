using System.Security.Claims;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;

namespace PrivatePaste.Server.Realtime;

public static class WebSocketEndpoint
{
    public static void MapWebSocketEndpoint(this IEndpointRouteBuilder app)
    {
        // Native clients send the bearer token in the upgrade request header, a browser as a subprotocol (see
        // WebSocketSubProtocols); either way only the device-token scheme applies.
        app.Map("/ws", async (HttpContext context, ClaimsPrincipal user, ConnectionHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest || user.DeviceId() is not { } deviceId)
                return Results.StatusCode(StatusCodes.Status400BadRequest);

            // A browser must get one of the subprotocols it offered back, and only the version one: never the token.
            using var socket = await context.WebSockets.AcceptWebSocketAsync(
                WebSocketSubProtocols.OffersVersion(context.Request) ? ProtocolHeaders.WebSocketSubProtocol : null);
            await hub.RunAsync(deviceId, socket, context.RequestAborted);
            return Results.Empty;
        }).RequireAuthorization(p => p
            .AddAuthenticationSchemes(AuthSchemes.DeviceToken)
            .RequireAuthenticatedUser());
    }
}
