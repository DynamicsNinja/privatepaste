using System.Net;
using System.Net.Http.Json;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Protocol;
using PrivatePaste.Tests.Images;

namespace PrivatePaste.Tests.Client;

public class ApiClientTests
{
    private static async Task<PrivatePasteApi> LoggedInAsync(PrivatePasteFactory server, string device = "PC-test")
    {
        var api = new PrivatePasteApi(server.CreateClient());
        var r = await api.LoginAsync(PrivatePasteFactory.Password, device, "windows");
        api.Token = r.Token;
        return api;
    }

    [Fact]
    public async Task Login_then_upload_and_list()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var clip = await api.UploadTextAsync("hello", null, false);
        Assert.Equal("hello", clip.Text);
        var page = await api.ListAsync(null, null);
        Assert.Equal("hello", page.Items[0].Text);
        Assert.Equal("PC-test", page.Items[0].SourceDeviceName);
    }

    [Fact]
    public async Task Wrong_password_throws_unauthorized()
    {
        using var server = new PrivatePasteFactory();
        var api = new PrivatePasteApi(server.CreateClient());
        await Assert.ThrowsAsync<UnauthorizedApiException>(() => api.LoginAsync("nope", "PC", "windows"));
    }

    [Fact]
    public async Task Revoked_token_throws_unauthorized()
    {
        using var server = new PrivatePasteFactory();
        var a = await LoggedInAsync(server, "A");
        var b = await server.LoginDeviceAsync("B");
        var aId = (await b.Client.GetFromJsonAsync<List<DeviceDto>>("/api/devices", ProtocolJson.Options))!
            .Single(d => d.Name == "A").Id;
        Assert.Equal(HttpStatusCode.NoContent, (await b.Client.DeleteAsync($"/api/devices/{aId}")).StatusCode);
        await Assert.ThrowsAsync<UnauthorizedApiException>(() => a.ListAsync(null, null));
    }

    [Fact]
    public async Task Image_upload_roundtrip()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var clip = await api.UploadImageAsync(TestImages.MakePng(10, 10).ToArray(), null, false);
        Assert.Equal(ClipKind.Image, clip.Kind);
        Assert.Equal(10, clip.Width);
        var thumb = await api.GetThumbnailAsync(clip.Id);
        Assert.True(TestImages.IsPng(thumb));
        Assert.True(TestImages.IsPng(await api.GetImageAsync(clip.Id)));
    }

    [Fact]
    public async Task Backfill_keeps_copied_at()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var t = server.Clock.GetUtcNow();
        var earlier = t - TimeSpan.FromHours(1);
        var text = await api.UploadTextAsync("old", earlier, true);
        Assert.Equal(earlier, text.LastCopiedAt);
        var img = await api.UploadImageAsync(TestImages.MakePng(4, 4).ToArray(), earlier, true);
        Assert.Equal(earlier, img.LastCopiedAt);
    }

    [Fact]
    public async Task Too_large_text_is_api_exception_413()
    {
        using var server = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_TEXT_KB"] = "1" });
        var api = await LoggedInAsync(server);
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.UploadTextAsync(new string('x', 2000), null, false));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.Status);
        Assert.IsNotType<UnauthorizedApiException>(ex);
    }

    [Fact]
    public async Task Sync_returns_changes_after_seq()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var first = await api.UploadTextAsync("one", null, false);
        var second = await api.UploadTextAsync("two", null, false);
        var sync = await api.SyncAsync(first.Seq);
        Assert.Equal(second.Id, Assert.Single(sync.Clips).Id);
        Assert.Equal(second.Seq, sync.LatestSeq);
        await api.DeleteAsync(first.Id);
        var after = await api.SyncAsync(second.Seq);
        Assert.Contains(first.Id, after.DeletedIds);
    }

    [Fact]
    public async Task Restore_bumps_and_delete_removes()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var a = await api.UploadTextAsync("a", null, false);
        server.Clock.Advance(TimeSpan.FromMinutes(1));
        await api.UploadTextAsync("b", null, false);
        server.Clock.Advance(TimeSpan.FromMinutes(1));
        var restored = await api.RestoreAsync(a.Id);
        Assert.True(restored.Seq > a.Seq);
        Assert.Equal("a", (await api.ListAsync(null, null)).Items[0].Text);
        await api.DeleteAsync(a.Id);
        Assert.Equal("b", Assert.Single((await api.ListAsync(null, null)).Items).Text);
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.DeleteAsync(a.Id));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task Logout_revokes_token()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        await api.LogoutAsync();
        await Assert.ThrowsAsync<UnauthorizedApiException>(() => api.ListAsync(null, null));
    }
}
