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
    private int _connects;
    private int _revoked;
    private volatile bool _paused;
    private readonly ConcurrentQueue<SyncError> _errors = new();

    /// <summary>When set, the next connect attempts consult it: return an exception to fail that attempt.</summary>
    private Func<int, Exception?>? _connectFailure;
    /// <summary>When set, wraps every socket the engine gets.</summary>
    private Func<WebSocket, WebSocket>? _wrapSocket;
    private readonly ConcurrentQueue<WebSocket> _sockets = new();

    private string CachePath => Path.Combine(_dir, "h.enc");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _apiA = new PrivatePasteApi(_server.CreateClient());
        var a = await _apiA.LoginAsync(PrivatePasteFactory.Password, "A", "windows");
        (_idA, _tokenA, _apiA.Token) = (a.DeviceId, a.Token, a.Token);
        _apiB = new PrivatePasteApi(_server.CreateClient());
        _apiB.Token = (await _apiB.LoginAsync(PrivatePasteFactory.Password, "B", "windows")).Token;
        _cache = new HistoryCache(CachePath, _key);
    }

    public async Task DisposeAsync()
    {
        foreach (var e in _engines) await e.DisposeAsync();
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<WebSocket> ConnectAsync(CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _connects);
        if (_connectFailure?.Invoke(n) is { } failure) throw failure;
        var client = _server.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + _tokenA;
        var socket = await client.ConnectAsync(new Uri(_server.Server.BaseAddress, "ws"), ct);
        if (_wrapSocket is { } wrap) socket = wrap(socket);
        _sockets.Enqueue(socket);
        return socket;
    }

    private SyncEngine CreateEngine(Backoff? backoff = null, IClipboardAccess? clipboard = null)
    {
        var coordinator = new ClipboardCoordinator(clipboard ?? _clip, _apiA, _cache, new OutboundQueue(Path.Combine(_dir, "q.enc"), _key),
            new EchoGuard(_time, TimeSpan.FromSeconds(30)), new ListNotifier(), _time, () => _paused);
        var engine = new SyncEngine(_apiA, _cache, coordinator, ConnectAsync, _idA, _time,
            backoff ?? new Backoff(new Random(1)), () => _paused);
        engine.StateChanged += (_, s) => _states.Enqueue(s);
        engine.Revoked += (_, _) => Interlocked.Increment(ref _revoked);
        engine.Error += (_, e) => _errors.Enqueue(e);
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

    private string[] Written() => _clip.Writes.ToArray().Select(w => ((ClipboardContent.Text)w).Value).ToArray();

    private bool Cached(string text) => _cache.Items.Any(i => i.Clip.Text == text);

    private int Count(SyncState s) => _states.Count(x => x == s);

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
            await _apiB.UploadTextAsync(t, null, false);
            _server.Clock.Advance(TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public async Task Catch_up_after_offline_applies_only_newest_once()
    {
        await UploadXyzWhileStoppedAsync();

        var engine = await StartConnectedAsync();

        Assert.Equal(["z"], Written());
        Assert.True(Cached("x") && Cached("y") && Cached("z"));
        await engine.StopAsync();
        Assert.Equal(["z"], Written());
    }

    [Fact]
    public async Task Catch_up_does_not_reapply_already_applied()
    {
        await UploadXyzWhileStoppedAsync();
        var first = await StartConnectedAsync();
        await first.StopAsync();
        Assert.Equal(["z"], Written());

        // Restart with the same (persisted) cache and a fresh clipboard.
        _cache = HistoryCache.Load(CachePath, _key);
        Assert.Equal(3, _cache.Items.Count);
        _clip = new FakeClipboard();
        await StartConnectedAsync();

        Assert.Empty(_clip.Writes);
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

    [Fact]
    public async Task Server_close_reconnects_and_backoff_is_reset_after_connecting()
    {
        // Two failures push the backoff to its 2nd step; a successful connect must reset it to 1 s.
        _connectFailure = n => n is 1 or 2 ? new WebSocketException("dropped") : null;
        var engine = CreateEngine(new Backoff(new Random(5)));
        engine.Start();
        await WaitUntilAsync(() => engine.State == SyncState.Connected, () => _time.Advance(TimeSpan.FromSeconds(1)));
        Assert.Equal(3, Volatile.Read(ref _connects));

        // Server-side drop (not a revoke): abort our socket from under the engine.
        Assert.True(_sockets.TryPeek(out var socket));
        socket.Abort();
        await WaitUntilAsync(() => engine.State == SyncState.Offline);
        _time.Advance(TimeSpan.FromSeconds(1.2));   // first backoff step again (<= 1.2 s)
        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        Assert.Equal(4, Volatile.Read(ref _connects));
        Assert.Equal(0, Volatile.Read(ref _revoked));
    }

    [Fact]
    public async Task Missing_pong_aborts_and_reconnects()
    {
        PingDroppingSocket? first = null;
        _wrapSocket = s => first is null ? first = new PingDroppingSocket(s) : s;
        var engine = await StartConnectedAsync();

        await WaitUntilAsync(() => Volatile.Read(ref _connects) == 2, () => _time.Advance(TimeSpan.FromSeconds(1)));
        await WaitUntilAsync(() => engine.State == SyncState.Connected);
        Assert.True(first!.PingsDropped >= 1);
        Assert.Contains(SyncState.Offline, _states);
    }

    [Fact]
    public async Task Pong_keeps_connection_alive()
    {
        var engine = await StartConnectedAsync();
        for (var i = 0; i < 60; i++)   // over two ping intervals, one second at a time
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }
        await _apiB.UploadTextAsync("still here", null, false);
        await WaitUntilAsync(() => Written().Contains("still here"));
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.Equal(SyncState.Connected, engine.State);
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

    [Fact]
    public async Task Paused_engine_disconnects_and_ignores_updates()
    {
        var engine = await StartConnectedAsync();
        Assert.True(_sockets.TryPeek(out var socket));

        _paused = true;
        _time.Advance(TimeSpan.FromSeconds(1));   // pause is polled every second
        await WaitUntilAsync(() => engine.State == SyncState.Paused);
        Assert.NotEqual(WebSocketState.Open, socket.State);

        await _apiB.UploadTextAsync("while paused", null, false);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(SyncState.Paused, engine.State);
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.Empty(_clip.Writes);
        Assert.False(Cached("while paused"));

        _paused = false;
        await WaitUntilAsync(() => engine.State == SyncState.Connected, () => _time.Advance(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, Volatile.Read(ref _connects));
        Assert.True(Cached("while paused"));
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

    /// <summary>Delegates to a real socket but silently drops outgoing pings, so no pong ever comes back.</summary>
    private sealed class PingDroppingSocket(WebSocket inner) : WebSocket
    {
        private int _dropped;
        public int PingsDropped => Volatile.Read(ref _dropped);
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;
        public override void Abort() => inner.Abort();
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => inner.CloseAsync(s, d, ct);
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => inner.CloseOutputAsync(s, d, ct);
        public override void Dispose() => inner.Dispose();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => inner.ReceiveAsync(buffer, ct);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            if (Encoding.UTF8.GetString(buffer).Contains("\"ping\""))
            {
                Interlocked.Increment(ref _dropped);
                return Task.CompletedTask;
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
