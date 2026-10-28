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

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => f(request, ct);
    }

    private static PrivatePasteApi Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f, TimeSpan? timeout = null)
    {
        var http = new HttpClient(new StubHandler(f)) { BaseAddress = new Uri("http://localhost/") };
        if (timeout is { } t) http.Timeout = t;
        return new PrivatePasteApi(http) { Token = "t" };
    }

    private static Task<HttpResponseMessage> Reply(HttpStatusCode code, string body, string contentType) =>
        Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType) });

    [Fact]
    public async Task Network_failure_is_http_request_exception()
    {
        var api = Stub((_, _) => throw new HttpRequestException("down"));
        await Assert.ThrowsAsync<HttpRequestException>(() => api.ListAsync(null, null));
    }

    [Fact]
    public async Task Timeout_is_http_request_exception_with_inner()
    {
        var api = Stub(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(); },
            TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => api.ListAsync(null, null));
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_is_operation_canceled()
    {
        var api = Stub(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(); });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.ListAsync(null, null, ct: cts.Token));
    }

    [Theory]
    [InlineData("<html>portal</html>", "text/html")]
    [InlineData("{not json", "application/json")]
    public async Task Unparseable_success_body_is_http_request_exception(string body, string type)
    {
        var api = Stub((_, _) => Reply(HttpStatusCode.OK, body, type));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => api.ListAsync(null, null));
        Assert.Equal("Unexpected response from server.", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Theory]
    [InlineData("{\"title\":\"T\",\"detail\":\"D\"}", "D")]
    [InlineData("{\"title\":\"T\"}", "T")]
    [InlineData("{}", "400 Bad Request")]
    [InlineData("oops", "400 Bad Request")]
    public async Task Error_message_comes_from_problem_details(string body, string expected)
    {
        var api = Stub((_, _) => Reply(HttpStatusCode.BadRequest, body, "application/problem+json"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.ListAsync(null, null));
        Assert.Equal(expected, ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
    }

    [Fact]
    public async Task Query_and_cursor_are_escaped()
    {
        var seen = new List<string>();
        var api = Stub((r, _) => { seen.Add(r.RequestUri!.OriginalString); return Reply(HttpStatusCode.OK, "{\"items\":[]}", "application/json"); });
        await api.ListAsync("a&b #c+d", "x&y z#+");
        Assert.Contains("q=a%26b%20%23c%2Bd", seen[0]);
        Assert.Contains("cursor=x%26y%20z%23%2B", seen[0]);
    }

    [Fact]
    public async Task Search_with_special_characters_finds_clip()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        await api.UploadTextAsync("a&b #c+d", null, false);
        var page = await api.ListAsync("a&b #c+d", null);
        Assert.Equal("a&b #c+d", Assert.Single(page.Items).Text);
    }

    [Fact]
    public async Task Per_call_timeouts_use_the_time_provider_and_scale_uploads_with_size()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var http = new HttpClient(new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); }))
        {
            BaseAddress = new Uri("http://localhost/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var api = new PrivatePasteApi(http, time) { Token = "t" };

        var list = api.ListAsync(null, null);
        var upload = api.UploadTextAsync(new string('x', 1024 * 1024), null, false);   // 1 MB: 30 s + 4 s
        time.Advance(TimeSpan.FromSeconds(30));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => list.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(ex.InnerException);
        await Task.Delay(50);
        Assert.False(upload.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(4));
        ex = await Assert.ThrowsAsync<HttpRequestException>(() => upload.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 31)]
    [InlineData(256 * 1024, 31)]
    [InlineData(1024 * 1024, 34)]
    [InlineData(20 * 1024 * 1024, 110)]
    [InlineData(1024L * 1024 * 1024, 300)]
    public void Upload_timeout_is_30_s_plus_1_s_per_256_KB_capped_at_5_min(long bytes, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PrivatePasteApi.UploadTimeout(bytes));
}
