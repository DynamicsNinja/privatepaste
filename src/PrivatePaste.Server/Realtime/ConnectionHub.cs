using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Clips;

namespace PrivatePaste.Server.Realtime;

/// <summary>
/// Tracks the connected WebSockets and pushes clip changes to all of them. Every socket has a bounded outbox
/// drained by its own writer loop, so broadcasting only enqueues and never waits on a slow client. A socket
/// whose outbox is full, or whose send fails or times out, is dropped and the others are unaffected.
/// When the host starts stopping, every socket gets a "going away" close and is torn down after a short grace
/// period, so open WebSockets never hold up shutdown.
/// </summary>
public sealed class ConnectionHub : IClipBroadcaster
{
    private const int MaxIncomingMessageBytes = 4096;
    private const int OutboxCapacity = 64;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);
    private const string ShutdownReason = "server shutting down";

    private static readonly byte[] PongBytes = JsonSerializer.SerializeToUtf8Bytes(new WsMessage(WsTypes.Pong), ProtocolJson.Options);

    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly DeviceRepository devices;
    private readonly ILogger<ConnectionHub> logger;
    private volatile bool _stopping;

    public ConnectionHub(DeviceRepository devices, ILogger<ConnectionHub> logger, IHostApplicationLifetime lifetime)
    {
        this.devices = devices;
        this.logger = logger;
        lifetime.ApplicationStopping.Register(CloseAllForShutdown);
    }

    /// <summary>One outbox entry: a text frame, or (when <see cref="Close"/> is set) a final close frame.</summary>
    private readonly record struct Outgoing(byte[]? Payload, (WebSocketCloseStatus Status, string Description)? Close);

    private sealed class Connection(Guid deviceId, WebSocket socket, CancellationTokenSource lifetime)
    {
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public Guid DeviceId { get; } = deviceId;
        public WebSocket Socket { get; } = socket;

        // The single writer loop is the only sender, which serializes frames and keeps their order.
        public Channel<Outgoing> Outbox { get; } = Channel.CreateBounded<Outgoing>(
            new BoundedChannelOptions(OutboxCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        public volatile bool Closing;
    }

    public Task ClipChangedAsync(ClipDto clip, bool live) =>
        Broadcast(new WsMessage(WsTypes.ClipCreated, Clip: clip, Live: live));

    public Task ClipDeletedAsync(Guid id, long seq) =>
        Broadcast(new WsMessage(WsTypes.ClipDeleted, Id: id, Seq: seq));

    /// <summary>Registers the socket, serves it until it closes, and unregisters it.</summary>
    public async Task RunAsync(Guid deviceId, WebSocket socket, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connection = new Connection(deviceId, socket, lifetime);
        _connections[connection] = 0;
        var writer = WriteLoopAsync(connection);
        try
        {
            // Registered first, then checked: a revoke that raced the handshake is caught here, and one
            // that comes later finds the registration in CloseDeviceAsync.
            if (_stopping)
                CloseForShutdown(connection);
            else if (!await devices.IsActiveAsync(deviceId))
                EnqueueClose(connection, WebSocketCloseStatus.PolicyViolation, "revoked");

            await ReceiveLoopAsync(connection, lifetime.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Client vanished or the host is shutting down.
        }
        finally
        {
            connection.Outbox.Writer.TryComplete();
            await writer; // bounded: every send has a timeout, and the loop never throws
            Drop(connection);
        }
    }

    /// <summary>Queues a policy-violation "revoked" close for every socket of the device. Does not wait for delivery.</summary>
    public Task CloseDeviceAsync(Guid deviceId)
    {
        foreach (var connection in _connections.Keys.Where(c => c.DeviceId == deviceId))
            EnqueueClose(connection, WebSocketCloseStatus.PolicyViolation, "revoked");
        return Task.CompletedTask;
    }

    private void CloseAllForShutdown()
    {
        // Set first, so a socket registering concurrently either is in the snapshot below or sees the flag.
        _stopping = true;
        foreach (var connection in _connections.Keys)
            CloseForShutdown(connection);
    }

    /// <summary>Queues a "going away" close, and tears the socket down if the client hasn't let go within the grace period.</summary>
    private void CloseForShutdown(Connection connection)
    {
        EnqueueClose(connection, WebSocketCloseStatus.EndpointUnavailable, ShutdownReason);
        try
        {
            connection.Lifetime.CancelAfter(ShutdownGrace);
        }
        catch (ObjectDisposedException)
        {
            // The connection already finished.
        }
    }

    private async Task ReceiveLoopAsync(Connection connection, CancellationToken ct)
    {
        var socket = connection.Socket;
        var buffer = new byte[MaxIncomingMessageBytes];
        var scratch = new byte[256];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var length = 0;
            var tooLarge = false;
            ValueWebSocketReceiveResult result;
            do
            {
                var free = buffer.AsMemory(length);
                // Once the buffer is full, keep draining into scratch so an oversize message is discarded.
                result = await socket.ReceiveAsync(free.IsEmpty ? scratch : free, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (free.IsEmpty)
                    tooLarge = true;
                else
                    length += result.Count;
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                EnqueueClose(connection, WebSocketCloseStatus.NormalClosure, null);
                return;
            }

            if (!tooLarge && result.MessageType == WebSocketMessageType.Text && IsPing(buffer.AsSpan(0, length)))
                Enqueue(connection, new Outgoing(PongBytes, null));
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

    private Task Broadcast(WsMessage message)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
            foreach (var connection in _connections.Keys)
                Enqueue(connection, new Outgoing(payload, null));
        }
        catch (Exception ex)
        {
            // A broadcast must never fail the HTTP request that triggered it.
            logger.LogWarning(ex, "WebSocket broadcast failed");
        }
        return Task.CompletedTask;
    }

    private void EnqueueClose(Connection connection, WebSocketCloseStatus status, string? description)
    {
        if (connection.Closing) return;
        Enqueue(connection, new Outgoing(null, (status, description ?? "")));
        connection.Closing = true;
    }

    /// <summary>Never blocks. A full outbox means the client is not keeping up, so the connection is dropped.</summary>
    private void Enqueue(Connection connection, Outgoing item)
    {
        if (connection.Closing) return;
        if (!connection.Outbox.Writer.TryWrite(item) && !connection.Outbox.Reader.Completion.IsCompleted)
        {
            logger.LogDebug("WebSocket outbox full; dropping the connection");
            Drop(connection);
        }
    }

    private async Task WriteLoopAsync(Connection connection)
    {
        try
        {
            await foreach (var item in connection.Outbox.Reader.ReadAllAsync())
            {
                // Dropping the connection also cancels a send that is stuck on a client that stopped reading.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(connection.Lifetime.Token);
                cts.CancelAfter(SendTimeout);
                if (item.Close is { } close)
                {
                    await connection.Socket.CloseOutputAsync(close.Status, close.Description, cts.Token);
                    return;
                }
                if (connection.Socket.State != WebSocketState.Open)
                    return;
                await connection.Socket.SendAsync(item.Payload!, WebSocketMessageType.Text, true, cts.Token);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "WebSocket send failed; dropping the connection");
        }
        finally
        {
            // Nothing is sent after a close frame or a failure; abort so the receive loop ends too.
            Drop(connection);
        }
    }

    private void Drop(Connection connection)
    {
        connection.Closing = true;
        if (!_connections.TryRemove(connection, out _))
            return;
        try
        {
            connection.Socket.Abort();
            // Abort alone doesn't always interrupt a pending receive; cancel it explicitly.
            connection.Lifetime.Cancel();
        }
        catch (ObjectDisposedException) { }
    }
}
