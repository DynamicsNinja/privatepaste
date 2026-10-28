using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Realtime;

namespace PrivatePaste.Tests.Realtime;

public class WebSocketTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Connects and, unless <paramref name="sync"/> is false, does a ping/pong round trip so the server has
    /// certainly registered the socket before the test acts.
    /// </summary>
    private static async Task<WebSocket> ConnectAsync(PrivatePasteFactory factory, string? token, bool sync = true)
    {
        var ws = factory.Server.CreateWebSocketClient();
        if (token is not null)
            ws.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        var socket = await ws.ConnectAsync(new Uri(factory.Server.BaseAddress, "/ws"), cts.Token);
        if (sync)
        {
            await SendTextAsync(socket, """{"type":"ping"}""");
            Assert.Equal(WsTypes.Pong, (await ReceiveMessageAsync(socket)).Type);
        }
        return socket;
    }

    private static async Task<(WebSocketReceiveResult Result, string Text)> ReceiveAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result, Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static async Task<WsMessage> ReceiveMessageAsync(WebSocket socket)
    {
        var (result, text) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonSerializer.Deserialize<WsMessage>(text, ProtocolJson.Options)!;
    }

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<ClipDto> PostText(HttpClient c, string text, bool backfill = false)
    {
        var r = await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, backfill), ProtocolJson.Options);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    [Fact]
    public async Task Connect_without_token_rejected()
    {
        using var factory = new PrivatePasteFactory();
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(factory, null));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(factory, "bogus"));
    }

    [Fact]
    public async Task Non_websocket_request_gets_400()
    {
        using var factory = new PrivatePasteFactory();
        var a = await factory.LoginAsync("A");
        Assert.Equal(HttpStatusCode.BadRequest, (await a.GetAsync("/ws")).StatusCode);
    }

    [Fact]
    public async Task Text_upload_is_pushed_live_to_other_device()
    {
        using var factory = new PrivatePasteFactory();
        var (a, _, _) = await factory.LoginDeviceAsync("A");
        var (_, _, tokenB) = await factory.LoginDeviceAsync("B");
        using var b = await ConnectAsync(factory, tokenB);

        var clip = await PostText(a, "hi");

        var msg = await ReceiveMessageAsync(b);
        Assert.Equal(WsTypes.ClipCreated, msg.Type);
        Assert.True(msg.Live);
        Assert.Equal(clip.Id, msg.Clip!.Id);
        Assert.Equal("hi", msg.Clip.Text);
    }

    [Fact]
    public async Task Sender_device_also_receives_its_own_clip()
    {
        using var factory = new PrivatePasteFactory();
        var (a, _, tokenA) = await factory.LoginDeviceAsync("A");
        using var wsA = await ConnectAsync(factory, tokenA);

        var clip = await PostText(a, "mine");

        Assert.Equal(clip.Id, (await ReceiveMessageAsync(wsA)).Clip!.Id);
    }

    [Fact]
    public async Task Backfill_upload_pushed_with_live_false()
    {
        using var factory = new PrivatePasteFactory();
        var (a, _, _) = await factory.LoginDeviceAsync("A");
        var (_, _, tokenB) = await factory.LoginDeviceAsync("B");
        using var b = await ConnectAsync(factory, tokenB);

        await PostText(a, "old", backfill: true);

        var msg = await ReceiveMessageAsync(b);
        Assert.Equal(WsTypes.ClipCreated, msg.Type);
        Assert.False(msg.Live);
    }

    [Fact]
    public async Task Image_backfill_fields_after_file_pushed_with_live_false()
    {
        using var factory = new PrivatePasteFactory();
        var (a, _, _) = await factory.LoginDeviceAsync("A");
        var (_, _, tokenB) = await factory.LoginDeviceAsync("B");
        using var b = await ConnectAsync(factory, tokenB);
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Images.TestImages.MakePng(8, 8).ToArray()), "file", "x.png" },
            { new StringContent("true"), "backfill" },
        };

        (await a.PostAsync("/api/clips/image", form)).EnsureSuccessStatusCode();

        var msg = await ReceiveMessageAsync(b);
        Assert.Equal(WsTypes.ClipCreated, msg.Type);
        Assert.False(msg.Live);
    }

    [Fact]
    public async Task Delete_is_pushed()
    {
        using var factory = new PrivatePasteFactory();
        var (a, _, _) = await factory.LoginDeviceAsync("A");
        var (_, _, tokenB) = await factory.LoginDeviceAsync("B");
        var clip = await PostText(a, "bye");
        using var b = await ConnectAsync(factory, tokenB);

        (await a.DeleteAsync($"/api/clips/{clip.Id}")).EnsureSuccessStatusCode();

        var msg = await ReceiveMessageAsync(b);
        Assert.Equal(WsTypes.ClipDeleted, msg.Type);
        Assert.Equal(clip.Id, msg.Id);
        Assert.True(msg.Seq > 0);
    }

    [Fact]
    public async Task Ping_gets_pong()
    {
        using var factory = new PrivatePasteFactory();
        var (_, _, token) = await factory.LoginDeviceAsync("A");
        using var ws = await ConnectAsync(factory, token);

        await SendTextAsync(ws, """{"type":"ping"}""");

        Assert.Equal(WsTypes.Pong, (await ReceiveMessageAsync(ws)).Type);
    }

    [Fact]
    public async Task Unknown_messages_are_ignored()
    {
        using var factory = new PrivatePasteFactory();
        var (_, _, token) = await factory.LoginDeviceAsync("A");
        using var ws = await ConnectAsync(factory, token);

        await SendTextAsync(ws, "not json");
        await SendTextAsync(ws, """{"type":"bogus"}""");
        await SendTextAsync(ws, """{"type":"ping"}""");

        Assert.Equal(WsTypes.Pong, (await ReceiveMessageAsync(ws)).Type);
    }

    [Fact]
    public async Task Revoking_device_closes_its_socket()
    {
        using var factory = new PrivatePasteFactory();
        var (a, _, _) = await factory.LoginDeviceAsync("A");
        var (_, idB, tokenB) = await factory.LoginDeviceAsync("B");
        using var b = await ConnectAsync(factory, tokenB);

        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/devices/{idB}")).StatusCode);

        var (result, _) = await ReceiveAsync(b);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
        Assert.Equal("revoked", result.CloseStatusDescription);
    }

    [Fact]
    public async Task Logout_closes_own_socket()
    {
        using var factory = new PrivatePasteFactory();
        var (a, _, token) = await factory.LoginDeviceAsync("A");
        using var ws = await ConnectAsync(factory, token);

        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsync("/api/auth/logout", null)).StatusCode);

        var (result, _) = await ReceiveAsync(ws);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal("revoked", result.CloseStatusDescription);
    }

    [Fact]
    public async Task Dead_socket_does_not_break_broadcast()
    {
        using var factory = new PrivatePasteFactory();
        var (a, _, _) = await factory.LoginDeviceAsync("A");
        var (_, _, tokenB) = await factory.LoginDeviceAsync("B");
        var (_, _, tokenC) = await factory.LoginDeviceAsync("C");
        using var b = await ConnectAsync(factory, tokenB);
        using var c = await ConnectAsync(factory, tokenC);
        b.Abort();

        await PostText(a, "still works");

        Assert.Equal("still works", (await ReceiveMessageAsync(c)).Clip!.Text);
    }

    [Fact]
    public async Task Revoking_one_device_leaves_other_sockets_open()
    {
        using var factory = new PrivatePasteFactory();
        var (client, idA, tokenA) = await factory.LoginDeviceAsync("A");
        var (_, _, tokenB) = await factory.LoginDeviceAsync("B");
        using var a = await ConnectAsync(factory, tokenA);
        using var b = await ConnectAsync(factory, tokenB);

        (await client.DeleteAsync($"/api/devices/{idA}")).EnsureSuccessStatusCode();

        Assert.Equal(WebSocketMessageType.Close, (await ReceiveAsync(a)).Result.MessageType);
        await SendTextAsync(b, """{"type":"ping"}""");
        Assert.Equal(WsTypes.Pong, (await ReceiveMessageAsync(b)).Type);
        Assert.Equal(WebSocketState.Open, b.State);
    }

    [Fact]
    public async Task Stalled_client_does_not_block_broadcast_or_other_clients()
    {
        using var factory = new PrivatePasteFactory();
        var (_, idStalled, _) = await factory.LoginDeviceAsync("stalled");
        var (_, idHealthy, _) = await factory.LoginDeviceAsync("healthy");
        var hub = factory.Services.GetRequiredService<ConnectionHub>();
        // In-memory pipes pause the writer at ~64 KB, so a never-read client makes a real send block.
        var (stalledServer, stalledClient) = CreateSocketPair();
        var (healthyServer, healthyClient) = CreateSocketPair();
        using var _1 = stalledServer;
        using var _2 = stalledClient;
        using var _3 = healthyServer;
        using var _4 = healthyClient;
        using var shutdown = new CancellationTokenSource();
        var stalledRun = hub.RunAsync(idStalled, stalledServer, shutdown.Token);
        var healthyRun = hub.RunAsync(idHealthy, healthyServer, shutdown.Token);
        // Round trip so the hub has certainly registered the healthy socket (the stalled one registers synchronously too).
        await SendTextAsync(healthyClient, """{"type":"ping"}""");
        Assert.Equal(WsTypes.Pong, (await ReceiveMessageAsync(healthyClient)).Type);

        const int clips = 5;
        var received = Task.Run(async () =>
        {
            var texts = new List<string>();
            while (texts.Count < clips)
                texts.Add((await ReceiveMessageAsync(healthyClient)).Clip!.Text![..9]);
            return texts;
        });

        for (var i = 0; i < clips; i++)
        {
            var clip = new ClipDto(Guid.NewGuid(), i + 1, ClipKind.Text, $"clip{i:D4} " + new string('x', 200_000),
                "hash" + i, null, null, 200_009, null, "PC", DateTimeOffset.UtcNow);
            var broadcast = hub.ClipChangedAsync(clip, true);
            Assert.True(broadcast.IsCompleted, $"broadcast {i} did not return immediately");
            await broadcast;
        }

        Assert.Equal(Enumerable.Range(0, clips).Select(i => $"clip{i:D4} "), await received);

        // Cancelling the request token (as a host shutdown would) lets both RunAsync calls finish.
        await shutdown.CancelAsync();
        await Task.WhenAll(stalledRun, healthyRun).WaitAsync(ReceiveTimeout);
    }

    [Fact]
    public async Task Host_shutdown_closes_sockets_with_going_away_and_stops_promptly()
    {
        var factory = new PrivatePasteFactory();
        try
        {
            var (_, _, token) = await factory.LoginDeviceAsync("A");
            using var ws = await ConnectAsync(factory, token);

            factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

            var (result, _) = await ReceiveAsync(ws);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, result.CloseStatus);
        }
        finally
        {
            // Disposing stops the host; it must not wait on the (now closed) socket.
            await Task.Run(factory.Dispose).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public async Task Socket_registered_after_shutdown_began_is_closed()
    {
        using var factory = new PrivatePasteFactory();
        var (_, idA, _) = await factory.LoginDeviceAsync("A");
        var hub = factory.Services.GetRequiredService<ConnectionHub>();
        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var _1 = serverSocket;
        using var _2 = clientSocket;

        var run = hub.RunAsync(idA, serverSocket, CancellationToken.None);

        var (result, _) = await ReceiveAsync(clientSocket);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, result.CloseStatus);
        await run.WaitAsync(ReceiveTimeout);
    }

    [Fact]
    public async Task Socket_registered_after_revoke_is_closed()
    {
        using var factory = new PrivatePasteFactory();
        var (_, idB, _) = await factory.LoginDeviceAsync("B");
        // Revoke without going through the endpoint, as if it raced the handshake and CloseDeviceAsync ran too early.
        await factory.Services.GetRequiredService<DeviceRepository>().RevokeAsync(idB);
        var hub = factory.Services.GetRequiredService<ConnectionHub>();
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var _1 = serverSocket;
        using var _2 = clientSocket;

        var run = hub.RunAsync(idB, serverSocket, CancellationToken.None);

        var (result, _) = await ReceiveAsync(clientSocket);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
        Assert.Equal("revoked", result.CloseStatusDescription);
        try { await run.WaitAsync(ReceiveTimeout); } catch (TimeoutException) { Assert.Fail("RunAsync did not finish; server state " + serverSocket.State); }
    }

    private static (WebSocket Server, WebSocket Client) CreateSocketPair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = WebSocket.CreateFromStream(
            new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
            new WebSocketCreationOptions { IsServer = true });
        var client = WebSocket.CreateFromStream(
            new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream()),
            new WebSocketCreationOptions { IsServer = false });
        return (server, client);
    }

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => output.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
