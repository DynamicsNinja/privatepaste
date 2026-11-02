namespace PrivatePaste.Protocol;

public static class ProtocolHeaders
{
    /// <summary>Sent on every request, including the WebSocket upgrade; the sync endpoints answer 426 without it.</summary>
    public const string Name = "X-PrivatePaste-Protocol";
    public const string Version = "2";

    /// <summary>
    /// For browsers, which can't set headers on a WebSocket upgrade: offering this subprotocol on <c>/ws</c> counts as the
    /// protocol header, and the server selects it when it accepts the socket.
    /// </summary>
    public const string WebSocketSubProtocol = "privatepaste.v2";

    /// <summary>
    /// For browsers: a second offered subprotocol, this prefix plus the device token, authenticates <c>/ws</c> when there
    /// is no Authorization header. The server never selects (echoes) it.
    /// </summary>
    public const string WebSocketAuthPrefix = "privatepaste.auth.";
}
