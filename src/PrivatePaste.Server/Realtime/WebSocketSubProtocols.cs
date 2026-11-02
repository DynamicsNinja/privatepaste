using PrivatePaste.Protocol;

namespace PrivatePaste.Server.Realtime;

/// <summary>
/// The browser extension's way onto <c>/ws</c>: a browser can't set the Authorization or protocol header on a WebSocket
/// upgrade, but it can offer subprotocols. <see cref="ProtocolHeaders.WebSocketSubProtocol"/> stands for the protocol
/// header and <see cref="ProtocolHeaders.WebSocketAuthPrefix"/> + token for the bearer token. Only on <c>/ws</c>; native
/// clients keep using the headers. The offered values carry a token: never log them.
/// </summary>
public static class WebSocketSubProtocols
{
    public static bool IsSocketPath(HttpRequest request) =>
        request.Path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase);

    /// <summary>The subprotocols offered on a <c>/ws</c> request (none elsewhere), trimmed.</summary>
    private static IEnumerable<string> Offered(HttpRequest request)
    {
        if (!IsSocketPath(request)) yield break;
        foreach (var value in request.Headers.SecWebSocketProtocol)
        {
            if (value is null) continue;
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return part;
        }
    }

    /// <summary>True when a <c>/ws</c> request offers <see cref="ProtocolHeaders.WebSocketSubProtocol"/>.</summary>
    public static bool OffersVersion(HttpRequest request) =>
        Offered(request).Contains(ProtocolHeaders.WebSocketSubProtocol, StringComparer.Ordinal);

    /// <summary>The device token offered as a subprotocol on a <c>/ws</c> request, or null.</summary>
    public static string? Token(HttpRequest request)
    {
        foreach (var part in Offered(request))
            if (part.StartsWith(ProtocolHeaders.WebSocketAuthPrefix, StringComparison.Ordinal))
                return part[ProtocolHeaders.WebSocketAuthPrefix.Length..];
        return null;
    }
}
