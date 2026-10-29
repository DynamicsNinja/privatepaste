using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Images;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Settings;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Protocol;
using PrivatePaste.Tests.Images;
using SkiaSharp;

namespace PrivatePaste.Tests.Client;

public partial class ClipboardCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-coord-" + Guid.NewGuid().ToString("N"));
    private readonly PrivatePasteFactory _server = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeClipboard _clip = new();
    private readonly ListNotifier _notes = new();
    private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private SwitchHandler _handler = null!;
    private PrivatePasteApi _api = null!;
    private HistoryCache _cache = null!;
    private OutboundQueue _queue = null!;
    private EchoGuard _echo = null!;
    private ClipboardCoordinator _c = null!;
    private bool _paused;
    private List<string> _blockedApps = [];

    public ClipboardCoordinatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task StartAsync(string device = "A")
    {
        _handler = new SwitchHandler(_server.Server.CreateHandler());
        _api = new PrivatePasteApi(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });
        _api.Token = (await _api.LoginAsync(PrivatePasteFactory.Password, device, "windows")).Token;
        _cache = new HistoryCache(Path.Combine(_dir, "h.enc"), _key);
        _queue = new OutboundQueue(Path.Combine(_dir, "q.enc"), _key);
        _echo = new EchoGuard(_time, TimeSpan.FromSeconds(30));
        _c = new ClipboardCoordinator(_clip, _api, _cache, _queue, _echo, _notes, _time, () => _paused,
            app => BlockedApps.Matches(_blockedApps, app));
    }

    private async Task<List<ClipDto>> ServerClipsAsync()
    {
        var client = await _server.LoginAsync("Observer");
        var page = await client.GetFromJsonAsync<ClipPage>("/api/clips?limit=50", ProtocolJson.Options);
        return page!.Items.ToList();
    }

    [Fact]
    public async Task Copy_uploads_live_and_caches()
    {
        await StartAsync();
        _clip.Current = new ClipboardContent.Text("hello");
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal("hello", Assert.Single(await ServerClipsAsync()).Text);
        Assert.Equal("hello", Assert.Single(_cache.Items).Clip.Text);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Remote_clip_written_to_clipboard_is_not_uploaded_back()
    {
        await StartAsync();
        var b = new PrivatePasteApi(_server.CreateClient());
        b.Token = (await b.LoginAsync(PrivatePasteFactory.Password, "B", "windows")).Token;
        var remote = await b.UploadTextAsync("from b", null, false);

        await _c.ApplyRemoteAsync(remote, default);
        Assert.Equal(new ClipboardContent.Text("from b"), Assert.Single(_clip.Writes));
        Assert.Equal(remote.LastCopiedAt, _cache.LastAppliedAt);

        await _c.OnClipboardChangedAsync(default);
        Assert.Equal("B", Assert.Single(await ServerClipsAsync()).SourceDeviceName);
    }

    [Fact]
    public async Task Echo_is_remembered_before_clipboard_write()
    {
        await StartAsync();
        var spy = new EchoSpyClipboard(() => _echo);
        var c = new ClipboardCoordinator(spy, _api, _cache, _queue, _echo, _notes, _time, () => false);
        var clip = new ClipDto(Guid.NewGuid(), 1, ClipKind.Text, "x", ContentHash.ForText("x"), null, null, 1, null, "B", _time.GetUtcNow());
        await c.ApplyRemoteAsync(clip, default);
        Assert.True(spy.WasRememberedAtWrite);
    }

    private sealed class EchoSpyClipboard(Func<EchoGuard> echo) : IClipboardAccess
    {
        public bool WasRememberedAtWrite { get; private set; }
        public event EventHandler? Changed { add { } remove { } }
        public Task<ClipboardContent?> ReadAsync(CancellationToken ct) => Task.FromResult<ClipboardContent?>(null);
        public Task WriteAsync(ClipboardContent content, CancellationToken ct)
        {
            WasRememberedAtWrite = echo().IsEcho(ContentHash.ForText("x"));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Echo_window_expires()
    {
        await StartAsync();
        _echo.Remember(ContentHash.ForText("again"));
        _time.Advance(TimeSpan.FromSeconds(31));
        _clip.Current = new ClipboardContent.Text("again");
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal("again", Assert.Single(await ServerClipsAsync()).Text);
    }

    [Fact]
    public async Task Paused_copy_is_never_uploaded_even_after_resume()
    {
        await StartAsync();
        _paused = true;
        _clip.Current = new ClipboardContent.Text("secret");
        await _c.OnClipboardChangedAsync(default);
        _paused = false;
        await _c.FlushQueueAsync(default);
        Assert.Empty(await ServerClipsAsync());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Offline_copy_is_queued_then_flushed_in_order_with_backfill()
    {
        await StartAsync();
        var t0 = _time.GetUtcNow();
        _handler.Offline = true;
        _clip.Current = new ClipboardContent.Text("a");
        await _c.OnClipboardChangedAsync(default);
        _time.Advance(TimeSpan.FromMinutes(1));
        _server.Clock.Advance(TimeSpan.FromMinutes(1));   // server clamps backfill copiedAt to its own now
        _clip.Current = new ClipboardContent.Text("b");
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal(2, _queue.Count);

        await _c.FlushQueueAsync(default);   // still offline: stops at first failure, keeps everything
        Assert.Equal(2, _queue.Count);

        _handler.Offline = false;
        await _c.FlushQueueAsync(default);
        Assert.Equal(0, _queue.Count);
        var clips = (await ServerClipsAsync()).OrderBy(c => c.LastCopiedAt).ToList();
        Assert.Equal(new[] { "a", "b" }, clips.Select(c => c.Text));
        Assert.Equal(t0, clips[0].LastCopiedAt);
        Assert.Equal(t0.AddMinutes(1), clips[1].LastCopiedAt);
    }

    [Fact]
    public async Task Flush_drops_rejected_item_with_one_notification_and_continues()
    {
        await StartAsync();
        _queue.Enqueue(new ClipboardContent.Image([1, 2, 3]), _time.GetUtcNow());   // server answers 400
        _queue.Enqueue(new ClipboardContent.Text("ok"), _time.GetUtcNow());
        await _c.FlushQueueAsync(default);
        Assert.Equal(0, _queue.Count);
        Assert.Single(_notes.Messages);
        Assert.Equal("ok", Assert.Single(await ServerClipsAsync()).Text);
    }

    [Fact]
    public async Task Unauthorized_propagates_and_item_stays_queued()
    {
        await StartAsync();
        _api.Token = "bogus";
        _queue.Enqueue(new ClipboardContent.Text("a"), _time.GetUtcNow());
        await Assert.ThrowsAsync<UnauthorizedApiException>(() => _c.FlushQueueAsync(default));
        Assert.Equal(1, _queue.Count);
        _clip.Current = new ClipboardContent.Text("live");
        await Assert.ThrowsAsync<UnauthorizedApiException>(() => _c.OnClipboardChangedAsync(default));
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task Oversized_text_is_skipped_with_one_notification()
    {
        await StartAsync();
        _clip.Current = new ClipboardContent.Text(new string('x', 1_048_577));
        await _c.OnClipboardChangedAsync(default);
        Assert.Empty(await ServerClipsAsync());
        Assert.Equal(0, _queue.Count);
        Assert.Contains("too large", Assert.Single(_notes.Messages));
    }

    [Fact]
    public async Task Oversized_image_is_skipped_with_one_notification()
    {
        await StartAsync();
        // A valid PNG padded past 20 MB (trailing bytes after IEND are ignored by decoders).
        var png = TestImages.MakePng(4, 4).ToArray().Concat(new byte[ClipboardCoordinator.MaxImageBytes]).ToArray();
        _clip.Current = new ClipboardContent.Image(png);
        await _c.OnClipboardChangedAsync(default);
        Assert.Empty(await ServerClipsAsync());
        Assert.Contains("too large", Assert.Single(_notes.Messages));
    }

    [Fact]
    public async Task Whitespace_only_is_ignored()
    {
        await StartAsync();
        _clip.Current = new ClipboardContent.Text("  \r\n\t ");
        await _c.OnClipboardChangedAsync(default);
        Assert.Empty(await ServerClipsAsync());
        Assert.Empty(_notes.Messages);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Image_copy_uploads_and_remote_image_is_applied_without_echo()
    {
        await StartAsync();
        var png = TestImages.MakePng(8, 8, 7).ToArray();
        _clip.Current = new ClipboardContent.Image(png);
        await _c.OnClipboardChangedAsync(default);
        var uploaded = Assert.Single(await ServerClipsAsync());
        Assert.Equal(ImageHashing.HashPng(png), uploaded.Hash);

        await _c.ApplyRemoteAsync(uploaded, default);
        Assert.IsType<ClipboardContent.Image>(Assert.Single(_clip.Writes));
        await _c.OnClipboardChangedAsync(default);   // clipboard holds the applied image -> echo, no re-upload
        Assert.Single(await ServerClipsAsync());
    }

    [Fact]
    public async Task Undecodable_remote_image_is_skipped_without_write_or_mark()
    {
        await StartAsync();
        var up = await _api.UploadImageAsync(TestImages.MakePng(4, 4).ToArray(), null, false);
        // The image endpoint answers 200 with a captive-portal style HTML page.
        var html = new PrivatePasteApi(new HttpClient(new HtmlHandler()) { BaseAddress = new Uri("http://localhost/") }) { Token = _api.Token };
        var c = new ClipboardCoordinator(_clip, html, _cache, _queue, _echo, _notes, _time, () => false);
        await c.ApplyRemoteAsync(up, default);
        Assert.Empty(_clip.Writes);
        Assert.Null(_cache.LastAppliedAt);
        Assert.False(_echo.IsEcho(up.Hash));
    }

    private sealed class HtmlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("<html>Please log in to the WiFi</html>")) });
    }

    [Fact]
    public void Image_hash_matches_protocol_vector()
    {
        using var bmp = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bmp.SetPixel(0, 0, new SKColor(255, 0, 0, 255));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Equal("8d833d22068fdf91eecbd0d310208548d1f9418c6513c572a7c7d01fc696c49a", ImageHashing.HashPng(data.ToArray()));
    }

    // ---------------------------------------------------------------- private copies and blocked apps

    [Fact]
    public async Task A_private_copy_is_not_uploaded_queued_or_cached()
    {
        await StartAsync();
        _clip.Current = new ClipboardContent.Text("hunter2") { IsPrivate = true, SourceApp = "keepass" };
        await _c.OnClipboardChangedAsync(default);
        _handler.Offline = true;   // offline, it must not be queued either
        _clip.Current = new ClipboardContent.Text("hunter3") { IsPrivate = true };
        await _c.OnClipboardChangedAsync(default);
        _clip.Current = new ClipboardContent.Image(TestImages.MakePng(4, 4).ToArray()) { IsPrivate = true };
        await _c.OnClipboardChangedAsync(default);
        _handler.Offline = false;
        await _c.FlushQueueAsync(default);

        Assert.Empty(_handler.Uploads);
        Assert.Empty(await ServerClipsAsync());
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_cache.Items);
        Assert.False(File.Exists(Path.Combine(_dir, "h.enc")));
        Assert.Empty(_notes.Messages);
    }

    [Fact]
    public async Task A_copy_from_a_blocked_app_is_skipped_case_insensitively()
    {
        await StartAsync();
        _blockedApps = ["KeePass.exe", "com.agilebits.onepassword7"];
        _clip.Current = new ClipboardContent.Text("from keepass") { SourceApp = "keepass" };
        await _c.OnClipboardChangedAsync(default);
        _clip.Current = new ClipboardContent.Text("from 1password") { SourceApp = "com.agilebits.onepassword7" };
        await _c.OnClipboardChangedAsync(default);
        _clip.Current = new ClipboardContent.Text("from notepad") { SourceApp = "notepad" };
        await _c.OnClipboardChangedAsync(default);
        _clip.Current = new ClipboardContent.Text("from unknown");
        await _c.OnClipboardChangedAsync(default);

        Assert.Equal(["from unknown", "from notepad"], (await ServerClipsAsync()).Select(c => c.Text));
        Assert.Equal(["from unknown", "from notepad"], _cache.Items.Select(i => i.Clip.Text));
    }

    [Fact]
    public async Task A_private_copy_still_counts_as_the_newest_local_copy()
    {
        await StartAsync();
        var readAt = _time.GetUtcNow();
        _clip.Current = new ClipboardContent.Text("secret") { IsPrivate = true };
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal(readAt, _c.LastLocalCaptureAt);
    }

    [Fact]
    public async Task Copying_the_previous_text_again_after_a_private_copy_uploads_it_again()
    {
        await StartAsync();
        _clip.Current = new ClipboardContent.Text("address");
        await _c.OnClipboardChangedAsync(default);
        _clip.Current = new ClipboardContent.Text("secret") { IsPrivate = true };
        await _c.OnClipboardChangedAsync(default);
        _clip.Current = new ClipboardContent.Text("address");
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal(2, _handler.Uploads.Count);   // it is on the clipboard again, so it is the newest copy again
    }

    [Fact]
    public async Task RecentSourceApps_keeps_the_last_ten_distinct_newest_first()
    {
        await StartAsync();
        _blockedApps = ["app3"];
        for (var i = 1; i <= 12; i++)
        {
            _clip.Current = new ClipboardContent.Text("t" + i) { SourceApp = "app" + i, IsPrivate = i == 5 };
            await _c.OnClipboardChangedAsync(default);
        }
        _clip.Current = new ClipboardContent.Text("again") { SourceApp = "app4" };
        await _c.OnClipboardChangedAsync(default);
        _clip.Current = new ClipboardContent.Text("no app");
        await _c.OnClipboardChangedAsync(default);

        Assert.Equal(["app4", "app12", "app11", "app10", "app9", "app8", "app7", "app6", "app5", "app3"], _c.RecentSourceApps);
    }

    [Fact]
    public async Task A_410_drops_the_queued_clip_without_a_toast()
    {
        await StartAsync();
        _queue.Enqueue(new ClipboardContent.Text("cleared"), _time.GetUtcNow());
        _queue.Enqueue(new ClipboardContent.Text("ok"), _time.GetUtcNow());
        _handler.Statuses.Enqueue(410);
        await _c.FlushQueueAsync(default);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_notes.Messages);
        Assert.Equal("ok", Assert.Single(await ServerClipsAsync()).Text);
    }

    [Fact]
    public void Undecodable_bytes_throw_invalid_data() =>
        Assert.Throws<InvalidDataException>(() => ImageHashing.HashPng(System.Text.Encoding.UTF8.GetBytes("<html/>")));
}
