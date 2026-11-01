using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;
using PrivatePaste.Tests.Images;

namespace PrivatePaste.Tests.Client;

/// <summary>End-to-end encryption on the desktop sync path, against the in-process server with a vault.</summary>
public sealed class E2eSyncTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly PrivatePasteFactory _server = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly List<Device> _devices = [];
    private VaultKey _key = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _key = await _server.VaultKeyAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _devices) await d.Engine.DisposeAsync();
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>One desktop: API (through a <see cref="SwitchHandler"/>), cache, queue, coordinator and engine.</summary>
    private sealed class Device
    {
        public required PrivatePasteApi Api { get; init; }
        public required SwitchHandler Handler { get; init; }
        public required HistoryCache Cache { get; init; }
        public required OutboundQueue Queue { get; init; }
        public required FakeClipboard Clipboard { get; init; }
        public required ClipboardCoordinator Coordinator { get; init; }
        public required SyncEngine Engine { get; init; }
        public ClipCodec? Codec { get; set; }
        public int Connects;
        public int KeyResets;
        public int CoordinatorKeyResets;
        public int UpdatesRequired;
    }

    private async Task<Device> DeviceAsync(string name, VaultKey? key = null, int capacity = 500)
    {
        var handler = new SwitchHandler(_server.Server.CreateHandler()) { Key = key ?? _key };
        var api = new PrivatePasteApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        var login = await api.LoginAsync(PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, name, "windows");
        api.Token = login.Token;
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        var cacheKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var cache = new HistoryCache(Path.Combine(dir, "h.enc"), cacheKey, capacity);
        var queue = new OutboundQueue(Path.Combine(dir, "q.enc"), cacheKey);
        var clipboard = new FakeClipboard();
        Device? device = null;
        var coordinator = new ClipboardCoordinator(clipboard, api, () => device!.Codec, cache, queue,
            new EchoGuard(_time, TimeSpan.FromSeconds(30)), new ListNotifier(), _time, () => false);
        var engine = new SyncEngine(api, cache, coordinator, () => device!.Codec, async ct =>
        {
            Interlocked.Increment(ref device!.Connects);
            var client = _server.Server.CreateWebSocketClient();
            client.ConfigureRequest = r =>
            {
                r.Headers.Authorization = "Bearer " + login.Token;
                r.Headers[ProtocolHeaders.Name] = ProtocolHeaders.Version;
            };
            return await client.ConnectAsync(new Uri(_server.Server.BaseAddress, "ws"), ct);
        }, login.DeviceId, _time, new Backoff(new Random(1)), () => false);
        device = new Device
        {
            Api = api, Handler = handler, Cache = cache, Queue = queue, Clipboard = clipboard, Coordinator = coordinator,
            Engine = engine, Codec = new ClipCodec(key ?? _key),
        };
        engine.KeyReset += (_, _) => Interlocked.Increment(ref device.KeyResets);
        engine.UpdateRequired += (_, _) => Interlocked.Increment(ref device.UpdatesRequired);
        coordinator.KeyReset += (_, _) => Interlocked.Increment(ref device.CoordinatorKeyResets);
        _devices.Add(device);
        return device;
    }

    private async Task<Device> ConnectedAsync(string name)
    {
        var d = await DeviceAsync(name);
        d.Engine.Start();
        await WaitUntilAsync(() => d.Engine.State == SyncState.Connected);
        return d;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > WaitTimeout) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    private static async Task CopyAsync(Device d, string text)
    {
        d.Clipboard.Current = new ClipboardContent.Text(text);
        await d.Coordinator.OnClipboardChangedAsync(default);
    }

    /// <summary>Resets encryption on the server as the owner would from the web, to a new key.</summary>
    private async Task<VaultKey> ResetVaultAsync()
    {
        var newKey = VaultKey.Create();
        var owner = await _server.LoginAsync("Web");
        (await owner.PostAsJsonAsync("/api/vault/reset", PrivatePasteFactory.RandomWrap(newKey.KeyId), ProtocolJson.Options))
            .EnsureSuccessStatusCode();
        return newKey;
    }

    // ---------------------------------------------------------------- decode at the edge

    [Fact]
    public async Task Sync_DecodesBeforeCaching()
    {
        var a = await ConnectedAsync("A");
        var b = await ConnectedAsync("B");

        await CopyAsync(a, "meet at noon");

        await WaitUntilAsync(() => b.Cache.Items.Any(i => i.Clip.Text == "meet at noon"));
        var cached = Assert.Single(b.Cache.Items).Clip;
        Assert.Equal(ClipKind.Text, cached.Kind);
        Assert.Equal(_key.KeyId, b.Cache.KeyId);
        await WaitUntilAsync(() => b.Clipboard.Writes.Count == 1);
        Assert.Equal(new ClipboardContent.Text("meet at noon"), b.Clipboard.Writes[0]);
        // A's own cache got the opened upload too; the server only ever held ciphertext.
        Assert.Equal("meet at noon", Assert.Single(a.Cache.Items).Clip.Text);
        var stored = Assert.Single((await a.Api.ListAsync(null)).Items);
        Assert.Null(stored.Text);
        Assert.NotNull(stored.Payload);
    }

    [Fact]
    public async Task Sync_ImageFromAnotherDeviceIsOpenedAppliedAndThumbnailed()
    {
        var a = await DeviceAsync("A");
        var b = await ConnectedAsync("B");
        var png = TestImages.MakePng(300, 200, 3).ToArray();
        a.Clipboard.Current = new ClipboardContent.Image(png);
        await a.Coordinator.OnClipboardChangedAsync(default);

        await WaitUntilAsync(() => b.Clipboard.Writes.Count == 1 && b.Cache.Items.Any(i => i.ThumbnailPng is not null));
        var written = Assert.IsType<ClipboardContent.Image>(b.Clipboard.Writes[0]);
        Assert.Equal(PrivatePaste.Client.Core.Images.ImageHashing.HashPng(png), PrivatePaste.Client.Core.Images.ImageHashing.HashPng(written.Png));
        var cached = Assert.Single(b.Cache.Items);
        Assert.Equal((300, 200), (cached.Clip.Width, cached.Clip.Height));
        Assert.Equal((256, 171), TestImages.Dims(cached.ThumbnailPng!));
    }

    [Fact]
    public async Task Coordinator_NeverAppliesUnknownKind()
    {
        var b = await ConnectedAsync("B");
        var phone = await _server.LoginAsync("Phone", "android");
        // Under the current key id but with a payload that doesn't open: a newer app's clip, as far as B can tell.
        using var other = VaultKey.Create();
        var meta = PrivatePasteFactory.TextMeta(_key, "unreadable") with
        {
            Payload = Convert.ToBase64String(SealedBox.Seal(other, "payload", "{}"u8)),
        };
        (await PrivatePasteFactory.PostClipAsync(phone, meta)).EnsureSuccessStatusCode();
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        await _server.UploadTextAsync(phone, "sentinel");   // messages are handled in order

        await WaitUntilAsync(() => b.Clipboard.Writes.Count == 1 && b.Cache.Items.Count == 2);
        Assert.Equal(new ClipboardContent.Text("sentinel"), Assert.Single(b.Clipboard.Writes));
        var unknown = b.Cache.Items.Single(i => i.Clip.Kind == ClipKind.Unknown).Clip;
        Assert.Null(unknown.Text);

        Assert.False(await b.Coordinator.ApplyRemoteAsync(unknown, default));
        Assert.False(await b.Coordinator.ApplyRemoteAsync(unknown with { Text = "server says" }, default));
        Assert.Single(b.Clipboard.Writes);
    }

    // ---------------------------------------------------------------- key reset

    [Fact]
    public async Task Sync_KeyResetRaisesEventAndStops()
    {
        var b = await ConnectedAsync("B");

        using var newKey = await ResetVaultAsync();

        await WaitUntilAsync(() => Volatile.Read(ref b.KeyResets) == 1);
        await WaitUntilAsync(() => b.Engine.State == SyncState.Offline);
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(36));   // well past any backoff
            await Task.Delay(20);
        }
        Assert.Equal(1, Volatile.Read(ref b.Connects));
        Assert.Equal(1, Volatile.Read(ref b.KeyResets));
        Assert.Equal(SyncState.Offline, b.Engine.State);
        b.Engine.Start();   // a stopped-for-good engine does not come back
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref b.Connects));
    }

    [Fact]
    public async Task Sync_ClipUnderAnotherKeyRaisesKeyReset()
    {
        var b = await DeviceAsync("B");
        using var newKey = await ResetVaultAsync();   // B was offline: it never saw the reset message
        await _server.UploadTextAsync(await _server.LoginAsync("Phone"), newKey, "sealed under the new key");

        b.Engine.Start();

        await WaitUntilAsync(() => Volatile.Read(ref b.KeyResets) == 1 && b.Engine.State == SyncState.Offline);
        Assert.Empty(b.Cache.Items);   // nothing from the new vault was taken in under the old key
        Assert.Empty(b.Clipboard.Writes);
    }

    [Fact]
    public async Task Coordinator_409OnFlush_ClearsQueueAndDoesNotRetry()
    {
        var a = await DeviceAsync("A");
        a.Handler.Offline = true;
        foreach (var text in new[] { "one", "two", "three" })
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await CopyAsync(a, text);
        }
        Assert.Equal(3, a.Queue.Count);
        using var newKey = await ResetVaultAsync();

        a.Handler.Offline = false;
        a.Engine.Start();   // connect, catch up, flush

        await WaitUntilAsync(() => Volatile.Read(ref a.KeyResets) == 1);
        await WaitUntilAsync(() => a.Engine.State == SyncState.Offline);
        Assert.Equal(0, a.Queue.Count);
        Assert.Equal(1, Volatile.Read(ref a.CoordinatorKeyResets));
        Assert.Equal([_key.KeyId], a.Handler.UploadKeyIds);   // the first 409, then nothing

        // Nothing more goes out under the refused key: not a flush, not a new copy (kept for the new key instead).
        await a.Coordinator.FlushQueueAsync(default);
        await CopyAsync(a, "after the reset");
        await a.Coordinator.FlushQueueAsync(default);
        Assert.Equal([_key.KeyId], a.Handler.UploadKeyIds);
        Assert.Equal(1, a.Queue.Count);
        Assert.Equal(1, Volatile.Read(ref a.KeyResets));
        Assert.Equal(1, Volatile.Read(ref a.CoordinatorKeyResets));

        // With the new key the waiting copy goes out.
        a.Codec = new ClipCodec(newKey);
        a.Handler.Key = newKey;
        await a.Coordinator.FlushQueueAsync(default);
        Assert.Equal(0, a.Queue.Count);
        Assert.Equal("after the reset", a.Handler.Uploads[^1]);
        Assert.Equal(newKey.KeyId, a.Handler.UploadKeyIds[^1]);
    }

    [Fact]
    public async Task Coordinator_LiveCopyRefusedAsResetIsDroppedAndRaisesKeyResetOnce()
    {
        var a = await DeviceAsync("A");
        using var newKey = await ResetVaultAsync();

        await CopyAsync(a, "one");
        await CopyAsync(a, "two");

        Assert.Equal(1, Volatile.Read(ref a.CoordinatorKeyResets));
        Assert.Equal([_key.KeyId], a.Handler.UploadKeyIds);
        Assert.Equal(1, a.Queue.Count);   // "two", kept for the new key; "one" was meant for the wiped history
        Assert.Empty(a.Cache.Items);
    }

    // ---------------------------------------------------------------- a key disposed mid-operation

    [Fact]
    public async Task Coordinator_KeyDisposedMidFlushKeepsTheClipQueuedAndSendsNothing()
    {
        using var key = VaultKey.FromBytes(_key.Export());
        var a = await DeviceAsync("A", key);
        a.Queue.Enqueue(new ClipboardContent.Text("queued"), _time.GetUtcNow());
        // The flush takes the codec, then asks the server for its limits: the key goes away right then (a sign-out).
        a.Handler.Respond = r =>
        {
            if (r.RequestUri!.AbsolutePath == "/api/server-info") key.Dispose();
            return null;
        };

        await a.Coordinator.FlushQueueAsync(default);

        Assert.Empty(a.Handler.Uploads);
        Assert.Equal(1, a.Queue.Count);
        Assert.True(a.Queue.TryPeek(out var item));
        Assert.Equal(0, item.Attempts);   // not a failed attempt: nothing was tried
    }

    [Fact]
    public async Task Coordinator_KeyDisposedMidApplyWritesNothing()
    {
        using var key = VaultKey.FromBytes(_key.Export());
        var b = await DeviceAsync("B", key);
        var (image, _, _) = await _server.UploadImageAsync(await _server.LoginAsync("Phone"), _key, TestImages.MakePng(8, 8).ToArray());
        var opened = b.Codec!.Decode(image);
        b.Handler.Respond = r =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/blobs/0")) key.Dispose();
            return null;
        };

        await Assert.ThrowsAsync<VaultKeyClosedException>(() => b.Coordinator.ApplyRemoteAsync(opened, default));

        Assert.Empty(b.Clipboard.Writes);
        Assert.Null(b.Cache.LastAppliedAt);
    }

    // ---------------------------------------------------------------- 426

    [Fact]
    public async Task Sync_UpdateRequiredRaisesEventAndStops()
    {
        var b = await DeviceAsync("B");
        b.Handler.Respond = r => r.RequestUri!.AbsolutePath == "/api/sync"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.UpgradeRequired)
            {
                Content = JsonContent.Create(new { detail = "Update PrivatePaste to keep syncing." }),
            }
            : null;

        b.Engine.Start();

        await WaitUntilAsync(() => Volatile.Read(ref b.UpdatesRequired) == 1 && b.Engine.State == SyncState.Offline);
        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref b.Connects));
        Assert.Equal(0, Volatile.Read(ref b.KeyResets));
    }

    [Fact]
    public async Task Sync_WithoutACodecStaysOfflineAndFetchesNothing()
    {
        var b = await DeviceAsync("B");
        var codec = b.Codec;
        b.Codec = null;   // locked
        b.Engine.Start();
        await WaitUntilAsync(() => b.Engine.State == SyncState.Offline);
        _time.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref b.Connects));

        b.Codec = codec;   // unlocked
        await WaitUntilAsync(() => b.Engine.State == SyncState.Connected, () => _time.Advance(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, Volatile.Read(ref b.Connects));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Action eachPoll)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > WaitTimeout) throw new TimeoutException("Condition not met in time.");
            eachPoll();
            await Task.Delay(10);
        }
    }

    // ---------------------------------------------------------------- older-clip search

    [Fact]
    public async Task OlderSearch_FindsBeyondCache()
    {
        var phone = await _server.LoginAsync("Phone");
        for (var i = 1; i <= 600; i++)
        {
            _server.Clock.Advance(TimeSpan.FromSeconds(1));
            await _server.UploadTextAsync(phone, i == 3 ? "clip 3 says aardvark" : $"clip {i}");
        }
        var b = await DeviceAsync("B");
        string? cursor = null;
        do
        {
            var page = await b.Api.ListAsync(cursor, 200);
            foreach (var clip in page.Items) b.Cache.Upsert(b.Codec!.Decode(clip));
            cursor = page.NextCursor;
        } while (cursor is not null && b.Cache.Items.Count < 500);
        Assert.Equal(500, b.Cache.Items.Count);
        Assert.Empty(b.Cache.Search("aardvark"));

        var found = new List<ClipDto>();
        await foreach (var clip in new OlderClipSearch(b.Api, b.Codec!).SearchAsync("AARDVARK", null, default))
            found.Add(clip);

        Assert.Equal("clip 3 says aardvark", Assert.Single(found).Text);
    }

    [Fact]
    public async Task OlderSearch_StartsAfterTheCursorAndSkipsUnknownClips()
    {
        var phone = await _server.LoginAsync("Phone");
        await _server.UploadTextAsync(phone, "needle old");
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        using var other = VaultKey.Create();
        var foreign = PrivatePasteFactory.TextMeta(_key, "needle unreadable") with
        {
            Payload = ClipPayload.Seal(other, new ClipPayload { Text = "needle unreadable" }),
        };
        (await PrivatePasteFactory.PostClipAsync(phone, foreign)).EnsureSuccessStatusCode();
        _server.Clock.Advance(TimeSpan.FromMinutes(1));
        await _server.UploadTextAsync(phone, "needle new");
        var b = await DeviceAsync("B");
        var first = await b.Api.ListAsync(null, 1);

        var found = new List<string?>();
        await foreach (var clip in new OlderClipSearch(b.Api, b.Codec!).SearchAsync("needle", first.NextCursor, default))
            found.Add(clip.Text);

        Assert.Equal(["needle old"], found);
    }

    // ---------------------------------------------------------------- protocol header

    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(string Request, bool HasHeader)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var has = request.Headers.TryGetValues(ProtocolHeaders.Name, out var v) && v.SequenceEqual([ProtocolHeaders.Version]);
            lock (Seen) Seen.Add(($"{request.Method} {request.RequestUri!.AbsolutePath}", has));
            return base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task Api_SendsProtocolHeaderEverywhere()
    {
        var recorder = new RecordingHandler(_server.Server.CreateHandler());
        var api = new PrivatePasteApi(new HttpClient(recorder) { BaseAddress = new Uri("http://localhost/") });
        api.Token = (await api.LoginAsync(PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "A", "windows")).Token;
        var codec = new ClipCodec(_key);

        Assert.NotNull(await api.GetServerInfoAsync());
        Assert.True((await api.GetMeAsync()).IsOwner);
        var wrap = await api.GetVaultAsync();
        Assert.Equal(_key.KeyId, wrap!.KeyId);
        await api.PutVaultAsync(wrap);
        var text = await api.UploadTextAsync(codec, "hello");
        var image = await api.UploadImageAsync(codec, TestImages.MakePng(8, 8).ToArray());
        Assert.Equal((8, 8), TestImages.Dims(codec.DecodeThumb(await api.GetThumbAsync(image.Id))));
        Assert.Equal((8, 8), TestImages.Dims(await codec.DecodeBlobAsync(await api.GetBlobAsync(image.Id, 0), default)));
        await api.ListAsync(null);
        await api.PinAsync(text.Id, true);
        await api.ListPinnedAsync(null);
        await api.RestoreAsync(text.Id);
        await api.SyncAsync(0);
        await api.DeleteAsync(text.Id);
        var handover = await api.CreateHandoverAsync([1, 2, 3]);
        Assert.Equal([1, 2, 3], await api.TakeHandoverAsync(handover.Id));
        Assert.Null(await api.TakeHandoverAsync(handover.Id));   // once only
        using var newKey = VaultKey.Create();
        await api.ResetVaultAsync(PrivatePasteFactory.RandomWrap(newKey.KeyId));
        await api.LogoutAsync();

        Assert.True(recorder.Seen.Count >= 19, string.Join(", ", recorder.Seen));
        Assert.All(recorder.Seen, s => Assert.True(s.HasHeader, s.Request));
    }
}
