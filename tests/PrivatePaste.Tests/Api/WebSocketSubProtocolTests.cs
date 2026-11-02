using System.Net.WebSockets;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Api;

/// <summary>The browser extension's way onto /ws: the version and the token offered as subprotocols, not headers.</summary>
public class WebSocketSubProtocolTests
{
    private static Uri Ws(PrivatePasteFactory f) => new(f.Server.BaseAddress, "/ws");

    private static Microsoft.AspNetCore.TestHost.WebSocketClient Browser(PrivatePasteFactory f, params string[] offered)
    {
        var client = f.Server.CreateWebSocketClient();
        foreach (var p in offered) client.SubProtocols.Add(p);
        return client;
    }

    [Fact]
    public async Task SubprotocolToken_OpensTheSocket_AndSelectsOnlyTheVersion()
    {
        using var f = new PrivatePasteFactory();
        var (_, _, token) = await f.LoginDeviceAsync("Edge", "extension");

        using var socket = await Browser(f, ProtocolHeaders.WebSocketSubProtocol, ProtocolHeaders.WebSocketAuthPrefix + token)
            .ConnectAsync(Ws(f), CancellationToken.None);

        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Equal(ProtocolHeaders.WebSocketSubProtocol, socket.SubProtocol);
    }

    [Fact]
    public async Task WrongSubprotocolToken_Gets401()
    {
        using var f = new PrivatePasteFactory();
        await f.LoginDeviceAsync("Edge", "extension");
        var client = Browser(f, ProtocolHeaders.WebSocketSubProtocol, ProtocolHeaders.WebSocketAuthPrefix + "not-a-token");

        // TestHost reports the refused handshake with its status code.
        var e = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(Ws(f), CancellationToken.None));
        Assert.Contains("401", e.Message);
    }

    [Fact]
    public async Task SubprotocolTokenWithoutVersion_Gets426()
    {
        using var f = new PrivatePasteFactory();
        var (_, _, token) = await f.LoginDeviceAsync("Edge", "extension");
        var client = Browser(f, ProtocolHeaders.WebSocketAuthPrefix + token);

        // TestHost reports the refused handshake with its status code.
        var e = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(Ws(f), CancellationToken.None));
        Assert.Contains("426", e.Message);
    }

    [Fact]
    public async Task HeaderClients_AreUnchanged_AndGetNoSubprotocol()
    {
        using var f = new PrivatePasteFactory();
        var (_, _, token) = await f.LoginDeviceAsync();
        var client = f.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.Headers.Authorization = "Bearer " + token;
            r.Headers[ProtocolHeaders.Name] = ProtocolHeaders.Version;
        };

        using var socket = await client.ConnectAsync(Ws(f), CancellationToken.None);

        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Null(socket.SubProtocol);
    }

    [Fact]
    public async Task SubprotocolToken_IsIgnoredOutsideTheSocket()
    {
        using var f = new PrivatePasteFactory();
        var (_, _, token) = await f.LoginDeviceAsync("Edge", "extension");
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(ProtocolHeaders.Name, ProtocolHeaders.Version);
        client.DefaultRequestHeaders.Add("Sec-WebSocket-Protocol", ProtocolHeaders.WebSocketAuthPrefix + token);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sync")).StatusCode);
    }

    [Fact]
    public async Task WebSessionCookie_DoesNotOpenTheSocket()
    {
        using var f = new PrivatePasteFactory();
        var web = f.CreateWebClient();
        var form = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/login");
        var login = await web.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = PrivatePasteFactory.OwnerName,
            ["Password"] = PrivatePasteFactory.Password,
            ["__RequestVerificationToken"] = form,
        }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, login.StatusCode);
        var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        using (var page = new HttpRequestMessage(HttpMethod.Get, "/"))
        {
            page.Headers.Add("Cookie", cookies);
            using var plain = f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
            Assert.Equal(System.Net.HttpStatusCode.OK, (await plain.SendAsync(page)).StatusCode);   // a working session
        }
        // The version offered as a subprotocol passes the protocol gate; the session cookie must still not authenticate.
        var client = Browser(f, ProtocolHeaders.WebSocketSubProtocol);
        client.ConfigureRequest = r => r.Headers.Cookie = cookies;

        var e = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(Ws(f), CancellationToken.None));
        Assert.Contains("401", e.Message);
    }

    [Fact]
    public async Task OfferedVersion_DoesNotStandInForTheHeaderOutsideTheSocket()
    {
        using var f = new PrivatePasteFactory();
        var (_, _, token) = await f.LoginDeviceAsync("Edge", "extension");
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("Sec-WebSocket-Protocol", ProtocolHeaders.WebSocketSubProtocol);

        Assert.Equal((System.Net.HttpStatusCode)426, (await client.GetAsync("/api/sync")).StatusCode);
    }

    [Fact]
    public async Task NonBearerAuthorization_WithSubprotocolToken_Gets401()
    {
        using var f = new PrivatePasteFactory();
        var (_, _, token) = await f.LoginDeviceAsync("Edge", "extension");
        var client = Browser(f, ProtocolHeaders.WebSocketSubProtocol, ProtocolHeaders.WebSocketAuthPrefix + token);
        client.ConfigureRequest = r => r.Headers.Authorization = "Basic eDp5";

        // A request that brings its own Authorization header is never authenticated from the subprotocol.
        var e = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(Ws(f), CancellationToken.None));
        Assert.Contains("401", e.Message);
    }
}
