using System.Net;
using System.Net.Http.Json;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;
using PrivatePaste.Tests.Images;

namespace PrivatePaste.Tests.Client;

public class ApiClientTests
{
    private static async Task<PrivatePasteApi> LoggedInAsync(PrivatePasteFactory server, string device = "PC-test")
    {
        var api = new PrivatePasteApi(server.CreateClient());
        var r = await api.LoginAsync(PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, device, "windows");
        api.Token = r.Token;
        return api;
    }

    private static async Task<ClipCodec> CodecAsync(PrivatePasteFactory server) => new(await server.VaultKeyAsync());

    private sealed class Capture : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new LoginResponse(Guid.NewGuid(), "t"), options: ProtocolJson.Options),
            };
        }
    }

    [Fact]
    public async Task Login_SendsUsername()
    {
        var capture = new Capture();
        var api = new PrivatePasteApi(new HttpClient(capture) { BaseAddress = new Uri("http://localhost") });
        await api.LoginAsync("Ana", "pw", "PC", "windows");
        Assert.Contains("\"username\":\"Ana\"", capture.Body);
    }

    [Fact]
    public async Task Login_then_upload_and_list()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var codec = await CodecAsync(server);
        var clip = await api.UploadTextAsync(codec, "hello");
        Assert.Equal("hello", clip.Text);
        var page = await api.ListOpenedAsync(codec);
        Assert.Equal("hello", page.Items[0].Text);
        Assert.Equal("PC-test", page.Items[0].SourceDeviceName);
    }

    [Fact]
    public async Task Pin_unpin_and_list_pinned()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var codec = await CodecAsync(server);
        var a = await api.UploadTextAsync(codec, "alpha");
        var b = await api.UploadTextAsync(codec, "beta");
        await api.UploadTextAsync(codec, "gamma");

        var pinnedA = await api.PinAsync(a.Id, true);
        Assert.NotNull(pinnedA.PinnedAt);
        Assert.True(pinnedA.Seq > a.Seq);
        server.Clock.Advance(TimeSpan.FromMinutes(1));
        await api.PinAsync(b.Id, true);

        var page = await api.ListPinnedOpenedAsync(codec);
        Assert.Equal(["beta", "alpha"], page.Items.Select(c => c.Text));
        var first = await api.ListPinnedOpenedAsync(codec, limit: 1);
        Assert.Equal(["beta"], first.Items.Select(c => c.Text));
        Assert.Equal(["alpha"], (await api.ListPinnedOpenedAsync(codec, first.NextCursor, limit: 1)).Items.Select(c => c.Text));

        var unpinned = await api.PinAsync(b.Id, false);
        Assert.Null(unpinned.PinnedAt);
        Assert.Equal(["alpha"], (await api.ListPinnedOpenedAsync(codec)).Items.Select(c => c.Text));
        Assert.Equal(HttpStatusCode.NotFound,
            (await Assert.ThrowsAsync<ApiException>(() => api.PinAsync(Guid.NewGuid(), true))).Status);
    }

    [Fact]
    public async Task Wrong_password_throws_unauthorized()
    {
        using var server = new PrivatePasteFactory();
        var api = new PrivatePasteApi(server.CreateClient());
        await Assert.ThrowsAsync<UnauthorizedApiException>(() => api.LoginAsync(PrivatePasteFactory.OwnerName, "nope", "PC", "windows"));
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
        await Assert.ThrowsAsync<UnauthorizedApiException>(() => a.ListAsync(null));
    }

    [Fact]
    public async Task Image_upload_roundtrip()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var codec = await CodecAsync(server);
        var clip = await api.UploadImageAsync(codec, TestImages.MakePng(10, 10).ToArray());
        Assert.Equal(ClipKind.Image, clip.Kind);
        Assert.Equal(10, clip.Width);
        Assert.True(clip.HasThumb);
        Assert.Equal(1, clip.BlobCount);
        var thumb = await api.GetThumbAsync(clip.Id);
        Assert.False(TestImages.IsPng(thumb));   // sealed
        Assert.True(TestImages.IsPng(codec.DecodeThumb(thumb)));
        Assert.True(TestImages.IsPng(await codec.DecodeBlobAsync(await api.GetBlobAsync(clip.Id, 0), default)));
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<ApiException>(() => api.GetBlobAsync(clip.Id, 1))).Status);
    }

    [Fact]
    public async Task Backfill_keeps_copied_at()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var codec = await CodecAsync(server);
        var t = server.Clock.GetUtcNow();
        var earlier = t - TimeSpan.FromHours(1);
        var text = await api.UploadTextAsync(codec, "old", earlier, true);
        Assert.Equal(earlier, text.LastCopiedAt);
        var img = await api.UploadImageAsync(codec, TestImages.MakePng(4, 4).ToArray(), earlier, true);
        Assert.Equal(earlier, img.LastCopiedAt);
    }

    [Fact]
    public async Task Too_large_text_is_api_exception_413()
    {
        using var server = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_TEXT_KB"] = "1" });
        var api = await LoggedInAsync(server);
        var codec = await CodecAsync(server);
        // Sealed against roomier limits than the server's, so the server is the one to refuse it.
        var upload = await codec.EncodeAsync(new ClipboardContent.Text(new string('x', 2000)), null, false, new ServerLimitsDto(1024, 20), default);
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.UploadClipAsync(upload));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.Status);
        Assert.IsNotType<UnauthorizedApiException>(ex);
    }

    [Fact]
    public async Task Sync_returns_changes_after_seq()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        var codec = await CodecAsync(server);
        var first = await api.UploadTextAsync(codec, "one");
        var second = await api.UploadTextAsync(codec, "two");
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
        var codec = await CodecAsync(server);
        var a = await api.UploadTextAsync(codec, "a");
        server.Clock.Advance(TimeSpan.FromMinutes(1));
        await api.UploadTextAsync(codec, "b");
        server.Clock.Advance(TimeSpan.FromMinutes(1));
        var restored = await api.RestoreAsync(a.Id);
        Assert.True(restored.Seq > a.Seq);
        Assert.Equal("a", codec.Decode(restored).Text);
        Assert.Equal("a", (await api.ListOpenedAsync(codec)).Items[0].Text);
        await api.DeleteAsync(a.Id);
        Assert.Equal("b", Assert.Single((await api.ListOpenedAsync(codec)).Items).Text);
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.DeleteAsync(a.Id));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task Logout_revokes_token()
    {
        using var server = new PrivatePasteFactory();
        var api = await LoggedInAsync(server);
        await api.LogoutAsync();
        await Assert.ThrowsAsync<UnauthorizedApiException>(() => api.ListAsync(null));
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
        await Assert.ThrowsAsync<HttpRequestException>(() => api.ListAsync(null));
    }

    [Fact]
    public async Task Timeout_is_http_request_exception_with_inner()
    {
        var api = Stub(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(); },
            TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => api.ListAsync(null));
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_is_operation_canceled()
    {
        var api = Stub(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(); });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.ListAsync(null, ct: cts.Token));
    }

    [Theory]
    [InlineData("<html>portal</html>", "text/html")]
    [InlineData("{not json", "application/json")]
    public async Task Unparseable_success_body_is_http_request_exception(string body, string type)
    {
        var api = Stub((_, _) => Reply(HttpStatusCode.OK, body, type));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => api.ListAsync(null));
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
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.ListAsync(null));
        Assert.Equal(expected, ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
    }

    [Fact]
    public async Task Cursor_is_escaped_and_no_query_is_sent()
    {
        var seen = new List<string>();
        var api = Stub((r, _) => { seen.Add(r.RequestUri!.OriginalString); return Reply(HttpStatusCode.OK, "{\"items\":[]}", "application/json"); });
        await api.ListAsync("x&y z#+");
        await api.ListPinnedAsync("x&y z#+");
        Assert.All(seen, url => Assert.Contains("cursor=x%26y%20z%23%2B", url));
        Assert.All(seen, url => Assert.DoesNotContain("q=", url));
    }

    [Fact]
    public async Task A_409_titled_encryption_was_reset_is_a_key_reset_and_a_426_is_update_required()
    {
        var reset = Stub((_, _) => Reply(HttpStatusCode.Conflict,
            "{\"title\":\"Encryption was reset\",\"detail\":\"Encryption was reset. Enter the new passphrase.\"}", "application/problem+json"));
        var e = await Assert.ThrowsAsync<KeyResetException>(() => reset.ListAsync(null));
        Assert.Equal("Encryption was reset. Enter the new passphrase.", e.Message);
        Assert.Equal(HttpStatusCode.Conflict, e.Status);

        var noVault = Stub((_, _) => Reply(HttpStatusCode.Conflict, "{\"title\":\"Conflict\",\"detail\":\"Set up encryption first.\"}",
            "application/problem+json"));
        var other = await Assert.ThrowsAsync<ApiException>(() => noVault.ListAsync(null));
        Assert.IsNotType<KeyResetException>(other);
        Assert.Equal("Set up encryption first.", other.Message);

        var old = Stub((_, _) => Reply(HttpStatusCode.UpgradeRequired, "{\"detail\":\"Update PrivatePaste to keep syncing.\"}",
            "application/problem+json"));
        var update = await Assert.ThrowsAsync<UpdateRequiredException>(() => old.SyncAsync(0));
        Assert.Equal("Update PrivatePaste to keep syncing.", update.Message);
    }

    [Fact]
    public async Task Server_info_and_vault_are_null_on_404()
    {
        var api = Stub((_, _) => Reply(HttpStatusCode.NotFound, "", "text/plain"));
        Assert.Null(await api.GetServerInfoAsync());
        Assert.Null(await api.GetVaultAsync());
        Assert.Null(await api.TakeHandoverAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Server_info_reports_the_limits_and_a_real_server_rejects_a_query()
    {
        using var server = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_TEXT_KB"] = "64", ["PP_MAX_IMAGE_MB"] = "5" });
        var api = await LoggedInAsync(server);
        var info = (await api.GetServerInfoAsync())!;
        Assert.Equal(2, info.Protocol);
        Assert.Equal(new ServerLimitsDto(64, 5), info.Limits);
        Assert.Null(await api.GetVaultAsync());
        Assert.True((await api.GetMeAsync()).IsOwner);
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

        var list = api.ListAsync(null);
        var payload = new string('A', 1024 * 1024);
        var upload = api.UploadClipAsync(new ClipUpload(new ClipUploadMeta("text", "k", "h", payload, 1, null, false), null, []));   // 1 MB: 30 s + 4 s
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
