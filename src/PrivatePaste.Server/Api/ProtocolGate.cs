using PrivatePaste.Protocol;
using PrivatePaste.Server.Realtime;

namespace PrivatePaste.Server.Api;

/// <summary>
/// Answers 426 to token clients that don't send the protocol 2 header on the clip, sync, history, vault and
/// WebSocket endpoints, so an old client is told to update instead of syncing something it can't read.
/// Web-session (cookie) requests pass: Razor form posts can't send the header, and the browser module is served
/// with the server. Requests without a bearer token pass too, to be answered 401 as before. On /ws a browser offers the
/// token and the version as subprotocols instead (<see cref="WebSocketSubProtocols"/>): an offered token makes it a token
/// request, and the offered version counts as the header.
/// </summary>
public static class ProtocolGate
{
    private static readonly string[] GatedPrefixes = ["/api/clips", "/api/sync", "/api/history", "/api/vault", "/ws"];

    public static IApplicationBuilder UseProtocolGate(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (IsGated(context.Request) && context.Request.Headers[ProtocolHeaders.Name] != ProtocolHeaders.Version
            && !WebSocketSubProtocols.OffersVersion(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await Results.Problem(statusCode: StatusCodes.Status426UpgradeRequired,
                detail: "Update PrivatePaste to keep syncing.").ExecuteAsync(context);
            return;
        }

        await next(context);
    });

    private static bool IsGated(HttpRequest request)
    {
        if (!request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && WebSocketSubProtocols.Token(request) is null)
            return false;
        foreach (var prefix in GatedPrefixes)
            if (request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
