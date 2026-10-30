using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Client;

public class SyncEngineTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-sync-" + Guid.NewGuid().ToString("N"));
    private readonly PrivatePasteFactory _server = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));   // CLIENT clock
    private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentQueue<SyncState> _states = new();
    private readonly List<SyncEngine> _engines = [];
    private FakeClipboard _clip = new();
    private PrivatePasteApi _apiA = null!;
    private PrivatePasteApi _apiB = null!;
    private Guid _idA;
    private string _tokenA = null!;
    private HistoryCache _cache = null!;
    /// <summary>The coordinator's own HTTP path (same device token as <see cref="_apiA"/>), so its uploads can be failed.</summary>
    private SwitchHandler _handlerA = null!;
    private PrivatePasteApi _coordApi = null!;
    private OutboundQueue _queue = null!;
    /// <summary>The coordinator of the engine created last.</summary>
    private ClipboardCoordinator _coordinator = null!;
    private readonly ListNotifier _notes = new();
    private int _connects;
    private int _revoked;
    private volatile bool _paused;
    private readonly ConcurrentQueue<SyncError> _errors = new();
    /// <summary>Every <see cref="SyncEngine.ClipApplied"/> of every engine, in order.</summary>
    private readonly ConcurrentQueue<ClipDto> _appliedEvents = new();

    /// <summary>When set, the next connect attempts consult it: return an exception to fail that attempt.</summary>
    private Func<int, Exception?>? _connectFailure;
    /// <summary>When it returns true for an attempt, that connect never completes (and ignores cancellation).</summary>
    private Func<int, bool>? _connectHangs;
    /// <summary>When set, wraps every socket the engine gets.</summary>
    private Func<WebSocket, WebSocket>? _wrapSocket;
    private readonly ConcurrentQueue<WebSocket> _sockets = new();
    /// <summary>Client (fake) clock reading at each connect attempt.</summary>
    private readonly ConcurrentQueue<DateTimeOffset> _connectTimes = new();

    private string CachePath => Path.Combine(_dir, "h.enc");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _apiA = new PrivatePasteApi(_server.CreateClient());
        var a = await _apiA.LoginAsync(PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "A", "windows");
        (_idA, _tokenA, _apiA.Token) = (a.DeviceId, a.Token, a.Token);
        _apiB = new PrivatePasteApi(_server.CreateClient());
        _apiB.Token = (await _apiB.LoginAsync(PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "B", "windows")).Token;
        _cache = new HistoryCache(CachePath, _key);
        _handlerA = new SwitchHandler(_server.Server.CreateHandler());
        _coordApi = new PrivatePasteApi(new HttpClient(_handlerA) { BaseAddress = new Uri("http://localhost/") }) { Token = _tokenA };
        _queue = new OutboundQueue(Path.Combine(_dir, "q.enc"), _key);
    }

    public async Task DisposeAsync()
    {
        foreach (var e in _engines) await e.DisposeAsync();
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<WebSocket> ConnectAsync(CancellationToken ct)
    {
        _connectTimes.Enqueue(_time.GetUtcNow());
        var n = Interlocked.Increment(ref _connects);
        if (_connectFailure?.Invoke(n) is { } failure) throw failure;
        if (_connectHangs?.Invoke(n) == true) return await new TaskCompletionSource<WebSocket>().Task;
        var client = _server.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + _tokenA;
        var socket = await client.ConnectAsync(new Uri(_server.Server.BaseAddress, "ws"), ct);
        if (_wrapSocket is { } wrap) socket = wrap(socket);
        _sockets.Enqueue(socket);
        return socket;
    }

    private SyncEngine CreateEngine(Backoff? backoff = null, IClipboardAccess? clipboard = null,
        Func<CancellationToken, Task<WebSocket>>? connect = null)
    {
        var coordinator = _coordinator = new ClipboardCoordinator(clipboard ?? _clip, _coordApi, _cache, _queue,
            new EchoGuard(_time, TimeSpan.FromSeconds(30)), _notes, _time, () => _paused);
        var engine = new SyncEngine(_apiA, _cache, coordinator, connect ?? ConnectAsync, _idA, _time,
            backoff ?? new Backoff(new Random(1)), () => _paused);
        engine.StateChanged += (_, s) => _states.Enqueue(s);
        engine.Revoked += (_, _) => Interlocked.Increment(ref _revoked);
        engine.Error += (_, e) => _errors.Enqueue(e);
        engine.ClipApplied += (_, c) => _appliedEvents.Enqueue(c);
        _engines.Add(engine);
        return engine;
    }

    private async Task<SyncEngine> StartConnectedAsync()
    {
        var engine = CreateEngine();
        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        return engine;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Action? eachPoll = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > WaitTimeout) throw new TimeoutException("Condition not met within 5 s.");
            eachPoll?.Invoke();
            await Task.Delay(10);
        }
    }

    private string[] Written() => _clip.Writes.ToArray().OfType<ClipboardContent.Text>().Select(w => w.Value).ToArray();

    private bool Cached(string text) => _cache.Items.Any(i => i.Clip.Text == text);

    private int Count(SyncState s) => _states.Count(x => x == s);

    private string[] Notified() => _appliedEvents.Select(c => c.Text ?? "").ToArray();

    // ---------------------------------------------------------------- ClipApplied (the "clip arrived" toast)

    [Fact]
    public async Task Clip_applied_is_raised_once_for_a_live_clip_from_another_device()
    {
        await StartConnectedAsync();

        await _apiB.UploadTextAsync("\n  first line of the clip  \nsecond line", null, false);

        await WaitUntilAsync(() => !_appliedEvents.IsEmpty);
        await Task.Delay(100);   // nothing else follows
        var clip = Assert.Single(_appliedEvents);
        Assert.Equal("B", clip.SourceDeviceName);
        Assert.Equal("windows", clip.SourceDevicePlatform);
        var toast = PrivatePaste.Desktop.Tray.ClipToast.For(clip);
        Assert.Equal("From B", toast.Title);
        Assert.Equal("first line of the clip", toast.Preview);
        Assert.Equal("windows", toast.Platform.Key);
    }

    [Fact]
    public async Task Clip_applied_is_not_raised_for_own_or_backfilled_clips()
    {
        await StartConnectedAsync();

        await _apiA.UploadTextAsync("mine", null, false);
        await _apiB.UploadTextAsync("old", null, backfill: true);
        await _apiB.UploadTextAsync("sentinel", null, false);   // messages are handled in order

        await WaitUntilAsync(() => Written().Contains("sentinel") && !_appliedEvents.IsEmpty);
        Assert.Equal(["sentinel"], Notified());
    }

    [Fact]
    public async Task Clip_applied_is_not_raised_when_the_clipboard_write_fails()
    {
        await StartConnectedAsync();
        _clip.WriteFailure = new IOException("clipboard locked");
        await _apiB.UploadTextAsync("locked", null, false);
        await WaitUntilAsync(() => !_errors.IsEmpty && Cached("locked"));

        _clip.WriteFailure = null;
        await _apiB.UploadTextAsync("works", null, false);
        await WaitUntilAsync(() => Written().Contains("works") && !_appliedEvents.IsEmpty);
        Assert.Equal(["works"], Notified());
    }

    [Fact]
    public async Task Clip_applied_is_not_raised_for_clips_that_arrive_while_paused()
    {
        var engine = await PauseWhileBUploadsAsync();
        Assert.Equal(["first"], Notified());

        _paused = false;
        await WaitUntilAsync(() => engine.State == SyncState.Connected, AdvanceWhilePaused(engine));
        Assert.Equal(["first"], Notified());   // the catch-up after a pause is history only
    }

    [Fact]
    public async Task Clip_applied_is_raised_once_for_the_single_catch_up_clip()
    {
        await PrimeAppliedAsync();
        await UploadXyzWhileStoppedAsync();

        await StartConnectedAsync();

        Assert.Equal(["first", "z"], Notified());
    }

    [Fact]
    public async Task Live_clip_from_other_device_is_applied()
    {
        var engine = await StartConnectedAsync();
        var changed = 0;
        engine.HistoryChanged += (_, _) => Interlocked.Increment(ref changed);

        await _apiB.UploadTextAsync("hi", null, false);

        await WaitUntilAsync(() => Written().Contains("hi") && Cached("hi"));
        Assert.Equal(["hi"], Written());
        await WaitUntilAsync(() => Volatile.Read(ref changed) > 0);
    }

    [Fact]
    public async Task Own_clip_is_cached_not_applied()
    {
        await StartConnectedAsync();

        await _apiA.UploadTextAsync("mine", null, false);
        await _apiB.UploadTextAsync("sentinel", null, false);   // messages are handled in order

        await WaitUntilAsync(() => Written().Contains("sentinel"));
        Assert.Equal(["sentinel"], Written());
        Assert.True(Cached("mine"));
    }

    [Fact]
    public async Task Backfill_clip_is_cached_not_applied()
    {
        await StartConnectedAsync();

        await _apiB.UploadTextAsync("old", null, backfill: true);
        await _apiB.UploadTextAsync("sentinel", null, false);

        await WaitUntilAsync(() => Written().Contains("sentinel"));
        Assert.Equal(["sentinel"], Written());
        Assert.True(Cached("old"));
    }

    [Fact]
    public async Task Delete_removes_from_cache()
    {
        await StartConnectedAsync();
        var clip = await _apiB.UploadTextAsync("gone", null, false);
        await WaitUntilAsync(() => Cached("gone"));

        await _apiB.DeleteAsync(clip.Id);

        await WaitUntilAsync(() => !Cached("gone"));
    }

    private async Task UploadXyzWhileStoppedAsync()
    {
        foreach (var t in new[] { "x", "y", "z" })
        {
            _server.Clock.Advance(TimeSpan.FromMinutes(1));
            await _apiB.UploadTextAsync(t, null, false);
        }
    }

    /// <summary>Runs the engine once so a live clip from B gets applied (LastAppliedAt is set), then stops it.</summary>
    private async Task PrimeAppliedAsync()
    {
        var engine = await StartConnectedAsync();
        await _apiB.UploadTextAsync("first", null, false);
        await WaitUntilAsync(() => Written().Contains("first"));
        await engine.StopAsync();
        Assert.NotNull(_cache.LastAppliedAt);
    }

    [Fact]
    public async Task Catch_up_after_offline_applies_only_newest_once()
    {
        await PrimeAppliedAsync();
        await UploadXyzWhileStoppedAsync();

        var engine = await StartConnectedAsync();

        Assert.Equal(["first", "z"], Written());
        Assert.True(Cached("x") && Cached("y") && Cached("z"));
        await engine.StopAsync();
        Assert.Equal(["first", "z"], Written());
    }

    [Fact]
    public async Task Catch_up_does_not_reapply_already_applied()
    {
        await PrimeAppliedAsync();
        await UploadXyzWhileStoppedAsync();
        var second = await StartConnectedAsync();
        await second.StopAsync();
        Assert.Equal(["first", "z"], Written());

        // Restart with the same (persisted) cache and a fresh clipboard.
        _cache = HistoryCache.Load(CachePath, _key);
        Assert.Equal(4, _cache.Items.Count);
        _clip = new FakeClipboard();
        await StartConnectedAsync();

        Assert.Empty(_clip.Writes);
    }

    /// <summary>The user copies something here (live upload through the coordinator) while the engine is not running.</summary>
    private async Task CopyLocallyAsync(string text)
    {
        _clip.Current = new ClipboardContent.Text(text);
        await _coordinator.OnClipboardChangedAsync(default);
    }

    [Fact]
    public async Task Catch_up_never_overwrites_a_newer_local_copy()
    {
        await PrimeAppliedAsync();
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        await _apiB.UploadTextAsync("remote", null, false);
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        var engine = CreateEngine();
        await CopyLocallyAsync("local, copied after remote");

        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);

        Assert.Equal(["first"], Written());
        Assert.True(Cached("remote"));
    }

    [Fact]
    public async Task Catch_up_does_not_apply_while_local_copies_are_queued()
    {
        await PrimeAppliedAsync();
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        await _apiB.UploadTextAsync("remote", null, false);
        var engine = CreateEngine();
        _handlerA.Offline = true;
        await CopyLocallyAsync("queued while offline");   // queued at the client clock, which is OLDER than "remote"
        Assert.Equal(1, _queue.Count);
        _handlerA.Offline = false;

        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);

        Assert.Equal(["first"], Written());
        Assert.Equal(0, _queue.Count);   // flushed after the catch-up
    }

    [Fact]
    public async Task Catch_up_applies_a_remote_clip_newer_than_everything_once()
    {
        await PrimeAppliedAsync();
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        var engine = CreateEngine();
        await CopyLocallyAsync("local, older");
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        await _apiB.UploadTextAsync("remote, newest", null, false);

        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        Assert.Equal(["first", "remote, newest"], Written());

        await engine.StopAsync();
        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        Assert.Equal(["first", "remote, newest"], Written());
    }

    [Fact]
    public async Task Catch_up_never_applies_a_backfilled_clip_older_than_a_local_copy()
    {
        await PrimeAppliedAsync();
        var firstAt = _cache.LastAppliedAt!.Value;
        _server.Clock.Advance(TimeSpan.FromMinutes(2));
        var engine = CreateEngine();
        await CopyLocallyAsync("local");
        var local = _cache.Items.Single(i => i.Clip.Text == "local").Clip;
        await _apiB.DeleteAsync(local.Id);   // gone from history, but still what the user has on the clipboard
        await _apiB.UploadTextAsync("backfilled", firstAt.AddMinutes(1), backfill: true);

        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);

        Assert.Equal(["first"], Written());
        Assert.True(Cached("backfilled") && !Cached("local"));
    }

    [Fact]
    public async Task A_private_copy_still_counts_as_the_newest_local_copy()
    {
        await PrimeAppliedAsync();
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        await _apiB.UploadTextAsync("remote", null, false);
        _time.Advance(TimeSpan.FromMinutes(2));   // the private copy is read after "remote" was copied
        var engine = CreateEngine();
        _clip.Current = new ClipboardContent.Text("password") { IsPrivate = true };
        await _coordinator.OnClipboardChangedAsync(default);

        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);

        Assert.Equal(["first"], Written());   // the password stays on the clipboard
        Assert.True(Cached("remote") && !Cached("password"));
    }

    [Fact]
    public async Task Catch_up_after_a_pin_applies_nothing()
    {
        // "while paused" is the newest clip but was never applied (the catch-up after a pause is history only).
        var engine = await PauseWhileBUploadsAsync();
        _paused = false;
        await WaitUntilAsync(() => engine.State == SyncState.Connected, AdvanceWhilePaused(engine));
        await engine.StopAsync();
        Assert.Equal(["first"], Written());

        // Pinning it (and the applied "first") gives both a new seq: the next catch-up brings them, and must not paste either.
        foreach (var text in new[] { "first", "while paused" })
            await _apiB.PinAsync(_cache.Items.Single(i => i.Clip.Text == text).Clip.Id, true);
        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        await WaitUntilAsync(() => _cache.Items.All(i => i.Clip.PinnedAt is not null));

        Assert.Equal(["first"], Written());
        Assert.Equal(["first"], Notified());
    }

    [Fact]
    public async Task A_transient_failure_while_connected_is_retried_by_the_periodic_flush()
    {
        var engine = await StartConnectedAsync();
        _handlerA.Statuses.Enqueue(503);
        await CopyLocallyAsync("retry me");
        Assert.Equal(1, _queue.Count);

        _time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50);
        Assert.Equal(1, _queue.Count);   // not due yet
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => _queue.Count == 0);

        Assert.Contains("retry me", (await _apiB.ListAsync(null, null)).Items.Select(c => c.Text));
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.Equal(SyncState.Connected, engine.State);
    }

    private bool HasThumbnail(Guid id) => _cache.Items.Any(i => i.Clip.Id == id && i.ThumbnailPng is { Length: > 0 });

    [Fact]
    public async Task A_live_image_from_another_device_gets_a_thumbnail_in_the_cache()
    {
        await StartConnectedAsync();
        var image = await _apiB.UploadImageAsync(PrivatePaste.Tests.Images.TestImages.MakePng(300, 200, 3).ToArray(), null, false);

        // The thumbnail fetch runs in the background beside the apply (image download + clipboard write), so either can finish first.
        await WaitUntilAsync(() => HasThumbnail(image.Id) && _clip.Writes.Count > 0);
        Assert.Single(_clip.Writes);
    }

    [Fact]
    public async Task Caught_up_images_get_thumbnails_too()
    {
        var image = await _apiB.UploadImageAsync(PrivatePaste.Tests.Images.TestImages.MakePng(40, 30, 4).ToArray(), null, false);
        await StartConnectedAsync();
        await WaitUntilAsync(() => HasThumbnail(image.Id));
    }

    [Fact]
    public async Task A_failed_thumbnail_fetch_is_swallowed()
    {
        _handlerA.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/thumbnail")
            ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) : null;
        var engine = await StartConnectedAsync();
        var image = await _apiB.UploadImageAsync(PrivatePaste.Tests.Images.TestImages.MakePng(8, 8, 5).ToArray(), null, false);
        await WaitUntilAsync(() => _clip.Writes.Count == 1 && _cache.Items.Any(i => i.Clip.Id == image.Id));

        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        await _apiB.UploadTextAsync("after the failed thumbnail", null, false);
        await WaitUntilAsync(() => Written().Contains("after the failed thumbnail"));
        Assert.False(HasThumbnail(image.Id));
        Assert.Equal(SyncState.Connected, engine.State);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task A_connect_that_never_completes_times_out_and_is_retried_after_backoff()
    {
        _connectHangs = n => n == 1;   // e.g. a TLS handshake or upgrade that stalls
        var engine = CreateEngine(connect: ct =>
            PrivatePaste.Desktop.DesktopHost.ConnectWithTimeoutAsync(ConnectAsync, _time, ct));
        engine.Start();
        await WaitUntilAsync(() => Volatile.Read(ref _connects) == 1);

        _time.Advance(PrivatePaste.Desktop.DesktopHost.WebSocketConnectTimeout - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.Equal(SyncState.Connecting, engine.State);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => engine.State == SyncState.Offline);
        await WaitUntilAsync(() => engine.State == SyncState.Connected, AdvanceWhileOffline(engine));

        Assert.Equal(2, Volatile.Read(ref _connects));
        Assert.Contains(_errors, e => e.Message == "Could not connect to the server." && e.Exception is TimeoutException);
    }

    [Fact]
    public async Task Nudge_drops_a_live_link_and_reconnects_at_once()
    {
        var engine = await StartConnectedAsync();
        var first = Assert.Single(_sockets);

        engine.Nudge();   // no clock advance: no backoff wait

        await WaitUntilAsync(() => Volatile.Read(ref _connects) == 2 && engine.State == SyncState.Connected);
        Assert.NotEqual(WebSocketState.Open, first.State);
        await _apiB.UploadTextAsync("after nudge", null, false);
        await WaitUntilAsync(() => Written().Contains("after nudge"));
    }

    [Fact]
    public async Task Nudge_cuts_the_backoff_wait_short()
    {
        _connectFailure = n => n == 1 ? new WebSocketException("network down") : null;
        var engine = CreateEngine();
        engine.Start();
        await WaitUntilAsync(() => Count(SyncState.Offline) == 1);   // waiting out the first backoff step
        Assert.Equal(1, Volatile.Read(ref _connects));

        engine.Nudge();

        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        Assert.Equal(2, Volatile.Read(ref _connects));
    }

    [Fact]
    public async Task First_catch_up_with_fresh_cache_is_history_only()
    {
        await UploadXyzWhileStoppedAsync();
        await _apiB.UploadTextAsync("old", null, backfill: true);

        await StartConnectedAsync();

        Assert.Empty(_clip.Writes);
        Assert.True(Cached("x") && Cached("y") && Cached("z") && Cached("old"));
        Assert.Null(_cache.LastAppliedAt);
    }

    [Fact]
    public async Task Revoked_raises_event_and_stops_reconnecting()
    {
        var engine = await StartConnectedAsync();

        (await _server.CreateClientWithToken(_apiB.Token!).DeleteAsync($"/api/devices/{_idA}")).EnsureSuccessStatusCode();

        await WaitUntilAsync(() => Volatile.Read(ref _revoked) == 1);
        await WaitUntilAsync(() => engine.State == SyncState.SignedOut);
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(36));   // 3 maximal backoff periods
            await Task.Yield();
        }
        await engine.StopAsync();
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.Equal(1, Volatile.Read(ref _revoked));
        Assert.Equal(SyncState.SignedOut, engine.State);
    }

    [Fact]
    public async Task Unauthorized_api_call_is_treated_as_revoked()
    {
        _tokenA = "bogus";      // the socket upgrade fails...
        _apiA.Token = "bogus";  // ...and the auth probe gets a 401
        var engine = CreateEngine();
        engine.Start();

        await WaitUntilAsync(() => engine.State == SyncState.SignedOut);
        Assert.Equal(1, Volatile.Read(ref _revoked));
        Assert.Equal(1, Volatile.Read(ref _connects));
    }

    [Fact]
    public async Task Server_drop_reconnects_with_backoff()
    {
        _connectFailure = n => n <= 2 ? new WebSocketException("dropped") : null;
        var expected = new Backoff(new Random(42));
        var (d1, d2) = (expected.Next(), expected.Next());
        var engine = CreateEngine(new Backoff(new Random(42)));
        engine.Start();

        await WaitUntilAsync(() => Count(SyncState.Offline) == 1);
        _time.Advance(d1 - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, Volatile.Read(ref _connects));            // not due yet
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => Count(SyncState.Offline) == 2);
        Assert.Equal(2, Volatile.Read(ref _connects));
        _time.Advance(d2 - TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, Volatile.Read(ref _connects));
        _time.Advance(TimeSpan.FromMilliseconds(1));

        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        Assert.Equal(3, Volatile.Read(ref _connects));
    }

    /// <summary>Advances the client clock 100 ms per poll, but only while the engine waits in Offline (its timer then exists).</summary>
    private Action AdvanceWhileOffline(SyncEngine engine) => () =>
    {
        if (engine.State == SyncState.Offline) _time.Advance(TimeSpan.FromMilliseconds(100));
    };

    [Fact]
    public async Task Backoff_is_reset_only_after_the_connection_stayed_up_one_ping_interval()
    {
        // Two failures push the backoff to its 3rd step; a connection that lasts 25 s resets it to 1 s.
        _connectFailure = n => n is 1 or 2 ? new WebSocketException("dropped") : null;
        var engine = CreateEngine(new Backoff(new Random(5)));
        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected, AdvanceWhileOffline(engine));
        Assert.Equal(3, Volatile.Read(ref _connects));

        var sock = Assert.Single(_sockets);
        _time.Advance(TimeSpan.FromSeconds(25));   // stays up one ping interval
        sock.Abort();                              // then a server-side drop (not a revoke)
        await WaitUntilAsync(() => engine.State == SyncState.Offline);
        _time.Advance(TimeSpan.FromSeconds(1.2));  // first backoff step again (<= 1.2 s)
        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        Assert.Equal(4, Volatile.Read(ref _connects));
        Assert.Equal(0, Volatile.Read(ref _revoked));
    }

    [Fact]
    public async Task Server_accepting_then_closing_immediately_keeps_growing_the_backoff()
    {
        _wrapSocket = s => new TestSocket(s) { CloseImmediately = true };
        var expected = new Backoff(new Random(9));
        var delays = Enumerable.Range(0, 3).Select(_ => expected.Next()).ToArray();
        var engine = CreateEngine(new Backoff(new Random(9)));
        engine.Start();

        await WaitUntilAsync(() => Volatile.Read(ref _connects) == 4, AdvanceWhileOffline(engine));

        var times = _connectTimes.ToArray();
        Assert.True(Count(SyncState.Connected) >= 3);   // every attempt did get connected
        for (var i = 0; i < 3; i++)
        {
            var gap = times[i + 1] - times[i];
            Assert.InRange(gap, delays[i], delays[i] + TimeSpan.FromMilliseconds(250));
        }
    }

    [Fact]
    public async Task Missing_pong_aborts_and_reconnects()
    {
        TestSocket? first = null;
        _wrapSocket = s => first is null ? first = new TestSocket(s) { DropPings = true } : s;
        var engine = await StartConnectedAsync();

        _time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => first!.PingsSent == 1);
        _time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => engine.State == SyncState.Offline);
        _time.Advance(TimeSpan.FromSeconds(1.2));   // first backoff step
        await WaitUntilAsync(() => Volatile.Read(ref _connects) == 2 && engine.State == SyncState.Connected);
        Assert.Contains(_errors, e => e.Message.Contains("pong"));
    }

    [Fact]
    public async Task Pong_keeps_connection_alive()
    {
        TestSocket? socket = null;
        _wrapSocket = s => socket = new TestSocket(s);
        var engine = await StartConnectedAsync();

        for (var i = 1; i <= 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(25));                    // ping due
            await WaitUntilAsync(() => socket!.PingsSent == i);
            await WaitUntilAsync(() => socket!.PongsHandled == i);      // engine has processed the pong
            _time.Advance(TimeSpan.FromSeconds(10));                    // past the pong timeout
        }
        await _apiB.UploadTextAsync("still here", null, false);
        await WaitUntilAsync(() => Written().Contains("still here"));
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.Equal(SyncState.Connected, engine.State);
    }

    [Fact]
    public async Task Throwing_event_handler_does_not_stop_the_engine()
    {
        var engine = CreateEngine();
        engine.StateChanged += (_, _) => throw new InvalidOperationException("bad handler");
        engine.HistoryChanged += (_, _) => throw new InvalidOperationException("bad handler");
        engine.Start();

        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        await _apiB.UploadTextAsync("hi", null, false);
        await WaitUntilAsync(() => Written().Contains("hi"));
        Assert.Equal(1, Volatile.Read(ref _connects));
    }

    [Fact]
    public async Task Clipboard_write_failure_is_reported_and_engine_keeps_running()
    {
        var engine = await StartConnectedAsync();
        _clip.WriteFailure = new IOException("clipboard locked");
        await _apiB.UploadTextAsync("locked", null, false);
        await WaitUntilAsync(() => !_errors.IsEmpty && Cached("locked"));
        Assert.Null(_cache.LastAppliedAt);

        _clip.WriteFailure = null;
        await _apiB.UploadTextAsync("works", null, false);
        await WaitUntilAsync(() => Written().Contains("works"));
        Assert.Equal(SyncState.Connected, engine.State);
        Assert.Equal(1, Volatile.Read(ref _connects));
    }

    [Fact]
    public async Task Slow_apply_does_not_trip_the_pong_timeout()
    {
        TestSocket? socket = null;
        _wrapSocket = s => socket = new TestSocket(s);
        var slow = new GatedClipboard();
        var engine = CreateEngine(clipboard: slow);
        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);

        await _apiB.UploadTextAsync("slow", null, false);
        await slow.Entered.Task.WaitAsync(WaitTimeout);
        for (var i = 0; i < 60; i++)   // a minute of client time while the write is stuck
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
        Assert.Equal(0, socket!.PingsSent);   // no ping (so no pong deadline) while the loop is busy
        slow.Release.SetResult();
        await WaitUntilAsync(() => slow.Written is not null);

        await _apiB.UploadTextAsync("next", null, false);
        await WaitUntilAsync(() => slow.Written is ClipboardContent.Text { Value: "next" });
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.Equal(SyncState.Connected, engine.State);
    }

    /// <summary>The first write blocks until released.</summary>
    private sealed class GatedClipboard : IClipboardAccess
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile ClipboardContent? Written;
        public event EventHandler? Changed { add { } remove { } }
        public Task<ClipboardContent?> ReadAsync(CancellationToken ct) => Task.FromResult(Written);
        public async Task WriteAsync(ClipboardContent content, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            Written = content;
        }
    }

    /// <summary>Advances the client clock 1 s per poll, but only while the engine sits in Paused (its poll timer then exists).</summary>
    private Action AdvanceWhilePaused(SyncEngine engine) => () =>
    {
        if (engine.State == SyncState.Paused) _time.Advance(TimeSpan.FromSeconds(1));
    };

    /// <summary>Connected engine with one applied live clip ("first"), then paused while B uploads "while paused".</summary>
    private async Task<SyncEngine> PauseWhileBUploadsAsync()
    {
        var engine = await StartConnectedAsync();
        Assert.True(_sockets.TryPeek(out var socket));
        await _apiB.UploadTextAsync("first", null, false);
        await WaitUntilAsync(() => Written().Contains("first"));
        _server.Clock.Advance(TimeSpan.FromMinutes(1));

        _paused = true;
        _time.Advance(TimeSpan.FromSeconds(1));   // pause is polled every second
        await WaitUntilAsync(() => engine.State == SyncState.Paused);
        Assert.NotEqual(WebSocketState.Open, socket.State);

        await _apiB.UploadTextAsync("while paused", null, false);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(SyncState.Paused, engine.State);
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.Equal(["first"], Written());
        Assert.False(Cached("while paused"));
        return engine;
    }

    [Fact]
    public async Task Paused_engine_disconnects_and_ignores_updates()
    {
        var engine = await PauseWhileBUploadsAsync();

        _paused = false;
        await WaitUntilAsync(() => engine.State == SyncState.Connected, AdvanceWhilePaused(engine));
        Assert.Equal(2, Volatile.Read(ref _connects));
        Assert.True(Cached("while paused"));
        Assert.Equal(["first"], Written());   // the first catch-up after a pause is history only
    }

    [Fact]
    public async Task Live_clip_after_resume_is_applied()
    {
        var engine = await PauseWhileBUploadsAsync();
        _paused = false;
        await WaitUntilAsync(() => engine.State == SyncState.Connected, AdvanceWhilePaused(engine));

        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        await _apiB.UploadTextAsync("after resume", null, false);
        await WaitUntilAsync(() => Written().Contains("after resume"));
        Assert.Equal(["first", "after resume"], Written());
    }

    [Fact]
    public async Task Resume_skip_survives_a_failed_reconnect()
    {
        var engine = await PauseWhileBUploadsAsync();
        _connectFailure = n => n == 2 ? new WebSocketException("dropped") : null;

        _paused = false;
        await WaitUntilAsync(() => engine.State == SyncState.Connected,
            () => { AdvanceWhilePaused(engine)(); AdvanceWhileOffline(engine)(); });
        Assert.Equal(3, Volatile.Read(ref _connects));
        Assert.True(Cached("while paused"));
        Assert.Equal(["first"], Written());

        // A later reconnect (not after a pause) applies catch-up as usual.
        await engine.StopAsync();
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        await _apiB.UploadTextAsync("later", null, false);
        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        Assert.Equal(["first", "later"], Written());
    }

    [Fact]
    public async Task Started_while_paused_stays_disconnected()
    {
        _paused = true;
        var engine = CreateEngine();
        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Paused);
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, Volatile.Read(ref _connects));
    }

    /// <summary>
    /// Delegates to a real socket and observes ping/pong. <see cref="PongsHandled"/> counts a pong only when the engine
    /// asks for the next message, i.e. after it has finished handling the pong. Optionally drops pings or closes at once.
    /// </summary>
    private sealed class TestSocket(WebSocket inner) : WebSocket
    {
        private int _pings, _pongs;
        private volatile bool _lastWasPong;
        public bool DropPings { get; init; }
        public bool CloseImmediately { get; init; }
        public int PingsSent => Volatile.Read(ref _pings);
        public int PongsHandled => Volatile.Read(ref _pongs);
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;
        public override void Abort() => inner.Abort();
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => inner.CloseAsync(s, d, ct);
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => inner.CloseOutputAsync(s, d, ct);
        public override void Dispose() => inner.Dispose();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (_lastWasPong)
            {
                _lastWasPong = false;
                Interlocked.Increment(ref _pongs);
            }
            if (CloseImmediately)
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.EndpointUnavailable, "bye");
            var r = await inner.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, r.Count).Contains("\"pong\""))
                _lastWasPong = true;
            return r;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            if (Encoding.UTF8.GetString(buffer).Contains("\"ping\""))
            {
                Interlocked.Increment(ref _pings);
                if (DropPings) return Task.CompletedTask;
            }
            return inner.SendAsync(buffer, type, end, ct);
        }
    }
}

internal static class FactoryClientExtensions
{
    public static HttpClient CreateClientWithToken(this PrivatePasteFactory factory, string token)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return c;
    }
}
