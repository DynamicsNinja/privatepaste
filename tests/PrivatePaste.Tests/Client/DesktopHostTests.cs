using System.Net;
using System.Net.Http.Json;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Desktop;
using PrivatePaste.Desktop.Logging;
using PrivatePaste.Desktop.Platform;
using PrivatePaste.Desktop.Tray;
using PrivatePaste.Desktop.ViewModels;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Client;

/// <summary>The composition root against the in-process server (real HTTP pipeline and WebSockets).</summary>
public class DesktopHostTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private const string ServerUrl = "http://localhost";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-host-" + Guid.NewGuid().ToString("N"));
    private readonly PrivatePasteFactory _server = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly ListNotifier _notes = new();
    private readonly List<DesktopHost> _hosts = [];
    private int _connects;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private DesktopHost NewHost(ISecretProtector? protector = null, Func<HttpMessageHandler>? handler = null)
    {
        var platform = new PlatformServices
        {
            Clipboard = _clipboard,
            Autostart = new NullAutostart(),
            SecretProtector = protector ?? new XorProtector(),
            PlatformName = "windows",
            AutostartLabel = "Start with Windows",
        };
        var transport = new HostTransport
        {
            CreateHandler = handler ?? (() => _server.Server.CreateHandler()),
            Connect = (uri, token, ct) =>
            {
                Interlocked.Increment(ref _connects);
                Assert.Equal("ws", uri.Scheme);
                var client = _server.Server.CreateWebSocketClient();
                client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
                return client.ConnectAsync(new UriBuilder(uri) { Scheme = "http" }.Uri, ct);
            },
        };
        var host = new DesktopHost(platform, _dir, _notes, new FileLog(Path.Combine(_dir, "logs", "privatepaste.log")),
            transport: transport);
        _hosts.Add(host);
        return host;
    }

    private string TokenPath => Path.Combine(_dir, "token.dat");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    private async Task<DesktopHost> SignedInHostAsync()
    {
        var host = NewHost();
        var outcome = await host.SignInAsync(ServerUrl, PrivatePasteFactory.Password, "PC-host");
        Assert.Equal(SignInResult.Ok, outcome.Result);
        await WaitUntilAsync(() => host.State == SyncState.Connected);
        return host;
    }

    [Theory]
    [InlineData("example.com", "Enter a full server address starting with https://")]
    [InlineData("http://example.com", "The server address must use https:// (http:// is only allowed for localhost).")]
    public async Task Invalid_url_returns_the_normalize_message(string url, string message)
    {
        var outcome = await NewHost().SignInAsync(url, "x", "PC");
        Assert.Equal(new SignInOutcome(SignInResult.InvalidUrl, message), outcome);
    }

    [Fact]
    public async Task Wrong_password()
    {
        var host = NewHost();
        Assert.Equal(SignInResult.WrongPassword, (await host.SignInAsync(ServerUrl, "nope", "PC")).Result);
        Assert.False(host.IsSignedIn);
        Assert.False(File.Exists(TokenPath));
    }

    [Fact]
    public async Task Rate_limited_and_unreachable()
    {
        var limited = NewHost(handler: () => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.Equal(SignInResult.RateLimited, (await limited.SignInAsync(ServerUrl, "x", "PC")).Result);

        var offline = NewHost(handler: () => new StubHandler(_ => throw new HttpRequestException("offline")));
        Assert.Equal(new SignInOutcome(SignInResult.Unreachable, null), await offline.SignInAsync(ServerUrl, "x", "PC"));
    }

    [Fact]
    public async Task Sign_in_stores_the_token_connects_and_resumes_after_a_restart()
    {
        var host = await SignedInHostAsync();
        Assert.True(File.Exists(TokenPath));
        Assert.NotNull(host.Settings.DeviceId);
        Assert.Equal("http://localhost/", host.Settings.ServerUrl);
        Assert.Equal("PC-host", host.Settings.DeviceName);
        Assert.NotNull(host.Api);
        Assert.NotNull(host.Coordinator);
        await host.DisposeAsync();
        _hosts.Remove(host);

        var again = NewHost();
        Assert.True(await again.StartAsync());
        await WaitUntilAsync(() => again.State == SyncState.Connected);
    }

    [Fact]
    public async Task A_network_change_or_resume_reconnects_at_once()
    {
        var host = await SignedInHostAsync();
        Assert.Equal(1, Volatile.Read(ref _connects));

        host.OnNetworkOrPowerChanged("test");

        await WaitUntilAsync(() => Volatile.Read(ref _connects) == 2 && host.State == SyncState.Connected);
    }

    [Fact]
    public async Task Without_a_token_start_asks_for_sign_in()
    {
        var host = NewHost();
        Assert.False(await host.StartAsync());
        Assert.Equal(SyncState.SignedOut, host.State);
    }

    [Fact]
    public async Task Sign_out_logs_out_deletes_the_token_and_clears_cache_and_queue()
    {
        var host = await SignedInHostAsync();
        var historyChanged = 0;
        host.HistoryChanged += (_, _) => Interlocked.Increment(ref historyChanged);
        var other = await _server.LoginAsync("Phone");
        (await other.PostAsJsonAsync("/api/clips/text", new TextClipUpload("from phone", null, false), ProtocolJson.Options))
            .EnsureSuccessStatusCode();
        await WaitUntilAsync(() => host.Cache.Items.Count == 1 && Volatile.Read(ref historyChanged) > 0);
        var signedOut = 0;
        host.SignedOut += (_, _) => signedOut++;

        await host.SignOutAsync();

        Assert.Equal(1, signedOut);
        Assert.False(host.IsSignedIn);
        Assert.Equal(SyncState.SignedOut, host.State);
        Assert.Null(host.Api);
        Assert.False(File.Exists(TokenPath));
        Assert.Empty(host.Cache.Items);
        Assert.False(File.Exists(Path.Combine(_dir, "cache.dat")));
        Assert.False(File.Exists(Path.Combine(_dir, "queue.dat")));
        Assert.Null(host.Settings.DeviceId);
        var devices = await other.GetFromJsonAsync<List<DeviceDto>>("/api/devices", ProtocolJson.Options);
        Assert.DoesNotContain(devices!, d => d.Name == "PC-host");   // the server-side logout happened
    }

    [Fact]
    public async Task A_device_revoked_on_the_server_is_signed_out()
    {
        var host = await SignedInHostAsync();
        var signedOut = 0;
        host.SignedOut += (_, _) => Interlocked.Increment(ref signedOut);
        var other = await _server.LoginAsync("Web");

        (await other.DeleteAsync($"/api/devices/{host.Settings.DeviceId}")).EnsureSuccessStatusCode();

        await WaitUntilAsync(() => Volatile.Read(ref signedOut) == 1);
        Assert.False(host.IsSignedIn);
        Assert.False(File.Exists(TokenPath));
        Assert.Contains("This device was signed out.", _notes.Messages);
    }

    [Fact]
    public async Task Resume_never_uploads_what_was_copied_while_paused()
    {
        var host = await SignedInHostAsync();
        await host.SetPausedAsync(true);
        Assert.True(host.Paused);
        Assert.True(NewSettingsStore().Load().Paused);   // persisted
        await WaitUntilAsync(() => host.State == SyncState.Paused);

        _clipboard.Current = new ClipboardContent.Text("copied while paused");
        _clipboard.RaiseChanged();
        await host.SetPausedAsync(false);
        Assert.False(NewSettingsStore().Load().Paused);
        await WaitUntilAsync(() => host.State == SyncState.Connected);
        _clipboard.RaiseChanged();   // a late duplicate event for the same content

        _clipboard.Current = new ClipboardContent.Text("after resume");
        _clipboard.RaiseChanged();
        var other = await _server.LoginAsync("Web");
        ClipPage? page = null;
        var until = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < until)
        {
            page = await other.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options);
            if (page!.Items.Any(c => c.Text == "after resume")) break;
            await Task.Delay(50);
        }
        Assert.Contains(page!.Items, c => c.Text == "after resume");
        Assert.DoesNotContain(page.Items, c => c.Text == "copied while paused");
    }

    [Fact]
    public void A_missing_cache_key_is_created_and_reused()
    {
        NewHost();
        var keyPath = Path.Combine(_dir, "cache.key");
        Assert.True(File.Exists(keyPath));
        var bytes = File.ReadAllBytes(keyPath);
        NewHost();
        Assert.Equal(bytes, File.ReadAllBytes(keyPath));
        Assert.Empty(_notes.Messages);
    }

    [Fact]
    public async Task An_unreadable_cache_key_is_never_overwritten()
    {
        var keyPath = Path.Combine(_dir, "cache.key");
        byte[] foreign = [1, 2, 3, 4, 5];
        File.WriteAllBytes(keyPath, foreign);
        File.WriteAllBytes(Path.Combine(_dir, "cache.dat"), [9, 9, 9]);

        var host = NewHost(new FailingUnprotect());

        Assert.Equal(foreign, File.ReadAllBytes(keyPath));
        Assert.Contains("Local history is unavailable this session.", _notes.Messages);
        Assert.Equal(SignInResult.Ok, (await host.SignInAsync(ServerUrl, PrivatePasteFactory.Password, "PC")).Result);
        Assert.Equal(foreign, File.ReadAllBytes(keyPath));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(_dir, "cache.dat")));
    }

    [Theory]
    [InlineData("http://localhost:5299/", "ws://localhost:5299/ws")]
    [InlineData("https://paste.example.com/", "wss://paste.example.com/ws")]
    [InlineData("https://example.com/pp/", "wss://example.com/pp/ws")]
    public void WebSocket_uri_follows_the_base_address(string baseAddress, string expected) =>
        Assert.Equal(new Uri(expected), DesktopHost.WebSocketUri(new Uri(baseAddress)));

    [Fact]
    public void Tray_status_text()
    {
        var server = new Uri("https://paste.ficdev.xyz/");
        Assert.Equal("Connected to paste.ficdev.xyz", TrayController.StatusText(true, false, SyncState.Connected, server));
        Assert.Equal("Connected to localhost:5299", TrayController.StatusText(true, false, SyncState.Connected, new Uri("http://localhost:5299/")));
        Assert.Equal("Offline, retrying", TrayController.StatusText(true, false, SyncState.Offline, server));
        Assert.Equal("Offline, retrying", TrayController.StatusText(true, false, SyncState.Connecting, server));
        Assert.Equal("Paused", TrayController.StatusText(true, true, SyncState.Paused, server));
        Assert.Equal("Paused", TrayController.StatusText(true, true, SyncState.Connected, server));
        Assert.Equal("Signed out", TrayController.StatusText(false, false, SyncState.SignedOut, server));
        Assert.Equal("Signed out", TrayController.StatusText(true, false, SyncState.SignedOut, server));
    }

    [Fact]
    public void Startup_options()
    {
        var plain = StartupOptions.Parse(["--minimized"], _ => null);
        Assert.True(plain.Minimized);
        Assert.False(plain.Isolated);
        Assert.Equal($"PrivatePaste.SingleInstance.{Environment.UserName}", plain.MutexName);

        var custom = StartupOptions.Parse(["--data-dir", _dir], _ => null);
        Assert.True(custom.Isolated);
        Assert.Equal(Path.GetFullPath(_dir), custom.DataDirectory);
        Assert.StartsWith(plain.MutexName + ".", custom.MutexName);

        var fromEnv = StartupOptions.Parse([], n => n == StartupOptions.DataDirEnvironmentVariable ? _dir : null);
        Assert.True(fromEnv.Isolated);
    }

    [Fact]
    public async Task Blocking_dispose_on_a_ui_thread_completes_and_saves_the_cache()
    {
        var host = await SignedInHostAsync();
        var other = await _server.LoginAsync("Phone");
        foreach (var text in new[] { "first", "last change" })
        {
            (await other.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, false), ProtocolJson.Options))
                .EnsureSuccessStatusCode();
            await WaitUntilAsync(() => host.Cache.Items.Any(i => i.Clip.Text == text));
        }
        _hosts.Remove(host);

        // A UI thread blocked in Wait never runs posted continuations: simulate it with a context that drops them.
        var finished = false;
        var elapsed = TimeSpan.Zero;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockedUiContext());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            finished = host.DisposeWithin(TimeSpan.FromSeconds(5));
            elapsed = sw.Elapsed;
        });
        thread.Start();
        thread.Join();

        Assert.True(finished, $"dispose did not finish (took {elapsed})");
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"took {elapsed}");
        var key = Convert.FromBase64String(new SecretFile(Path.Combine(_dir, "cache.key"), new XorProtector()).Read()!);
        var saved = global::PrivatePaste.Client.Core.Cache.HistoryCache.Load(Path.Combine(_dir, "cache.dat"), key);
        Assert.Contains(saved.Items, i => i.Clip.Text == "last change");
    }

    [Fact]
    public async Task Sign_out_during_an_upload_leaves_cache_and_queue_empty()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = NewHost(handler: () => new HoldUploads(_server.Server.CreateHandler(), entered, release.Task));
        Assert.Equal(SignInResult.Ok, (await host.SignInAsync(ServerUrl, PrivatePasteFactory.Password, "PC-host")).Result);
        await WaitUntilAsync(() => host.State == SyncState.Connected);

        _clipboard.Current = new ClipboardContent.Text("in flight");
        _clipboard.RaiseChanged();
        await entered.Task.WaitAsync(WaitTimeout);
        await host.SignOutAsync();
        release.SetResult();
        await Task.Delay(500);

        Assert.Empty(host.Cache.Items);
        Assert.False(File.Exists(Path.Combine(_dir, "queue.dat")));
        Assert.False(File.Exists(Path.Combine(_dir, "cache.dat")));
    }

    [Fact]
    public async Task History_changed_is_raised_for_local_uploads_and_sign_out()
    {
        var host = await SignedInHostAsync();
        var changes = 0;
        host.HistoryChanged += (_, _) => Interlocked.Increment(ref changes);

        _clipboard.Current = new ClipboardContent.Text("copied here");
        _clipboard.RaiseChanged();
        await WaitUntilAsync(() => host.Cache.Items.Any(i => i.Clip.Text == "copied here") && Volatile.Read(ref changes) > 0);

        var before = Volatile.Read(ref changes);
        await host.SignOutAsync();
        await WaitUntilAsync(() => Volatile.Read(ref changes) > before);
    }

    private sealed class BlockedUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }   // never pumped while the thread is blocked
        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException();
    }

    /// <summary>Holds text uploads until released (honouring cancellation late), after signalling that one arrived.</summary>
    private sealed class HoldUploads(HttpMessageHandler inner, TaskCompletionSource entered, Task release) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/api/clips/text"))
            {
                entered.TrySetResult();
                try { await release.WaitAsync(ct); }
                catch (OperationCanceledException)
                {
                    await Task.Delay(300, CancellationToken.None);   // a transport that notices cancellation late
                    throw;
                }
            }
            return await base.SendAsync(request, ct);
        }
    }

    private global::PrivatePaste.Client.Core.Settings.SettingsStore NewSettingsStore() => new(_dir);

    private sealed record DeviceDto(Guid Id, string Name);

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] protectedData) => Protect(protectedData);
    }

    private sealed class FailingUnprotect : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => throw new System.Security.Cryptography.CryptographicException("not yours");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
