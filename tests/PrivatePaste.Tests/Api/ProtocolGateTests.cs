using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Api;

public class ProtocolGateTests
{
    private const string UpdateDetail = "Update PrivatePaste to keep syncing.";

    [Fact]
    public async Task ServerInfo_ReportsProtocolKindsAndLimits()
    {
        using var f = new PrivatePasteFactory(new Dictionary<string, string>
        {
            ["PP_MAX_TEXT_KB"] = "64",
            ["PP_MAX_IMAGE_MB"] = "3",
        });

        var info = await f.CreateClient().GetFromJsonAsync<ServerInfoDto>("/api/server-info", ProtocolJson.Options);

        Assert.NotNull(info);
        Assert.Equal(2, info.Protocol);
        Assert.Equal(["text", "link", "image", "file"], info.Kinds);
        Assert.Equal(new ServerLimitsDto(64, 3, 25), info.Limits);
    }

    [Fact]
    public async Task ServerInfo_lists_file_kind_and_limit()
    {
        using var f = new PrivatePasteFactory(new Dictionary<string, string>
        {
            ["PP_MAX_TEXT_KB"] = "64",
            ["PP_MAX_IMAGE_MB"] = "3",
        });

        var info = await f.CreateClient().GetFromJsonAsync<ServerInfoDto>("/api/server-info", ProtocolJson.Options);

        Assert.Contains("file", info!.Kinds);
        Assert.Equal(new ServerLimitsDto(64, 3, 25), info.Limits);

        using var small = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_FILE_MB"] = "7" });
        var smallInfo = await small.CreateClient().GetFromJsonAsync<ServerInfoDto>("/api/server-info", ProtocolJson.Options);
        Assert.Equal(7, smallInfo!.Limits.FileMb);
    }

    [Fact]
    public async Task LegacyClient_Gets426OnClipsSyncAndWs()
    {
        using var f = new PrivatePasteFactory();
        var (legacy, _, token) = await f.LoginDeviceAsync(legacy: true);

        foreach (var path in new[] { "/api/clips", "/api/sync" })
        {
            var response = await legacy.GetAsync(path);
            Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
            Assert.Equal(UpdateDetail, await DetailAsync(response));
        }

        var ws = f.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri(f.Server.BaseAddress, "/ws"), CancellationToken.None));
        // Header-bearing clients still pass the gate (the same device connects below).
        var current = f.Server.CreateWebSocketClient();
        current.ConfigureRequest = r =>
        {
            r.Headers.Authorization = "Bearer " + token;
            r.Headers[ProtocolHeaders.Name] = ProtocolHeaders.Version;
        };
        using var socket = await current.ConnectAsync(new Uri(f.Server.BaseAddress, "/ws"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task LegacyClient_CanStillLogOut()
    {
        using var f = new PrivatePasteFactory();
        var legacy = await f.LoginAsync(legacy: true);

        Assert.Equal(HttpStatusCode.NoContent, (await legacy.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task WebSession_PassesTheGateWithoutTheHeader()
    {
        using var f = new PrivatePasteFactory();
        var web = await f.LoginWebAsync();

        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Me_ReportsOwnerFlag()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");

        var owner = await (await f.LoginAsync()).GetFromJsonAsync<MeDto>("/api/me", ProtocolJson.Options);
        var ana = await (await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1"))
            .GetFromJsonAsync<MeDto>("/api/me", ProtocolJson.Options);

        Assert.Equal(new MeDto(PrivatePasteFactory.OwnerName, true), owner);
        Assert.Equal(new MeDto("Ana", false), ana);
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("detail").GetString();
}
