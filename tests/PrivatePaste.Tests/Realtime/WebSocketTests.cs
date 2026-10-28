using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Realtime;

public class WebSocketTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private static async Task<WebSocket> ConnectAsync(PrivatePasteFactory factory, string? token)
    {
        var ws = factory.Server.CreateWebSocketClient();
        if (token is not null)
            ws.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        return await ws.ConnectAsync(new Uri(factory.Server.BaseAddress, "/ws"), cts.Token);
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
}
