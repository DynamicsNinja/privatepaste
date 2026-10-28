using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Clips;

namespace PrivatePaste.Server.Realtime;

/// <summary>
/// Tracks the connected WebSockets and pushes clip changes to all of them. Broadcasts never throw:
/// a socket whose send fails or times out is dropped and the others are unaffected.
/// </summary>
public sealed class ConnectionHub(ILogger<ConnectionHub> logger) : IClipBroadcaster
{
    private const int MaxIncomingMessageBytes = 4096;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private static readonly byte[] PongBytes = JsonSerializer.SerializeToUtf8Bytes(new WsMessage(WsTypes.Pong), ProtocolJson.Options);

    private readonly ConcurrentDictionary<Connection, byte> _connections = new();

    private sealed class Connection(Guid deviceId, WebSocket socket)
    {
        public Guid DeviceId { get; } = deviceId;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    public Task ClipChangedAsync(ClipDto clip, bool live) =>
        BroadcastAsync(new WsMessage(WsTypes.ClipCreated, Clip: clip, Live: live));

    public Task ClipDeletedAsync(Guid id, long seq) =>
        BroadcastAsync(new WsMessage(WsTypes.ClipDeleted, Id: id, Seq: seq));

    /// <summary>Registers the socket, serves it until it closes, and unregisters it.</summary>
    public async Task RunAsync(Guid deviceId, WebSocket socket, CancellationToken ct)
    {
        var connection = new Connection(deviceId, socket);
        _connections[connection] = 0;
        try
        {
            await ReceiveLoopAsync(connection, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Client vanished or the host is shutting down.
        }
        finally
        {
            Drop(connection);
        }
    }

    /// <summary>Closes every socket of the device with a policy-violation "revoked" status.</summary>
    public async Task CloseDeviceAsync(Guid deviceId)
    {
        var targets = _connections.Keys.Where(c => c.DeviceId == deviceId).ToList();
        await Task.WhenAll(targets.Select(async connection =>
        {
            try
            {
                using var cts = new CancellationTokenSource(SendTimeout);
                await connection.SendLock.WaitAsync(cts.Token);
                try
                {
                    await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "revoked", cts.Token);
                }
                finally
                {
                    connection.SendLock.Release();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Closing a revoked socket failed");
            }
            finally
            {
                // Don't wait for the peer's close handshake; the close frame has already been sent.
                Drop(connection);
            }
        }));
    }

    private async Task ReceiveLoopAsync(Connection connection, CancellationToken ct)
    {
        var socket = connection.Socket;
        var buffer = new byte[MaxIncomingMessageBytes];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var length = 0;
            var tooLarge = false;
            ValueWebSocketReceiveResult result;
            do
            {
                var free = buffer.AsMemory(length);
                // Keep reading into an empty-but-valid slice once full so oversize frames are drained and discarded.
                result = await socket.ReceiveAsync(free.IsEmpty ? new byte[256] : free, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (free.IsEmpty)
                    tooLarge = true;
                else
                    length += result.Count;
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return;
            }

            if (!tooLarge && result.MessageType == WebSocketMessageType.Text && IsPing(buffer.AsSpan(0, length)))
                await SendAsync(connection, PongBytes);
        }
    }

    private static bool IsPing(ReadOnlySpan<byte> payload)
    {
        try
        {
            var reader = new Utf8JsonReader(payload);
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == WsTypes.Ping;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task BroadcastAsync(WsMessage message)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
            await Task.WhenAll(_connections.Keys.Select(c => SendAsync(c, payload)));
        }
        catch (Exception ex)
        {
            // A broadcast must never fail the HTTP request that triggered it.
            logger.LogWarning(ex, "WebSocket broadcast failed");
        }
    }

    /// <summary>Sends one text frame; on any failure the connection is dropped. Never throws.</summary>
    private async Task SendAsync(Connection connection, byte[] payload)
    {
        try
        {
            using var cts = new CancellationTokenSource(SendTimeout);
            await connection.SendLock.WaitAsync(cts.Token);
            try
            {
                if (connection.Socket.State != WebSocketState.Open)
                    throw new WebSocketException("Socket is not open.");
                await connection.Socket.SendAsync(payload, WebSocketMessageType.Text, true, cts.Token);
            }
            finally
            {
                connection.SendLock.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "WebSocket send failed; dropping the connection");
            Drop(connection);
        }
    }

    private void Drop(Connection connection)
    {
        if (!_connections.TryRemove(connection, out _))
            return;
        try { connection.Socket.Abort(); }
        catch (ObjectDisposedException) { }
    }
}
