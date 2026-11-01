namespace PrivatePaste.Protocol;

public static class ProtocolHeaders
{
    /// <summary>Sent on every request, including the WebSocket upgrade; the sync endpoints answer 426 without it.</summary>
    public const string Name = "X-PrivatePaste-Protocol";
    public const string Version = "2";
}
