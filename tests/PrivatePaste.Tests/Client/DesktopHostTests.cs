using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Desktop;
using PrivatePaste.Desktop.Logging;
using PrivatePaste.Desktop.Platform;
using PrivatePaste.Desktop.Tray;
using PrivatePaste.Desktop.ViewModels;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;
using PrivatePaste.Server.Storage;

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
    private VaultKey _vaultKey = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _vaultKey = await _server.VaultKeyAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private DesktopHost NewHost(ISecretProtector? protector = null, Func<HttpMessageHandler>? handler = null,
        PrivatePasteFactory? server = null, string? dir = null, bool unlocked = true, Func<bool>? wsOffline = null,
        TimeProvider? time = null)
    {
        server ??= _server;
        dir ??= _dir;
        Directory.CreateDirectory(dir);
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
            CreateHandler = handler ?? (() => server.Server.CreateHandler()),
            Connect = (uri, token, ct) =>
            {
                Interlocked.Increment(ref _connects);
                Assert.Equal("ws", uri.Scheme);
                if (wsOffline?.Invoke() == true) throw new WebSocketException("offline");
                var client = server.Server.CreateWebSocketClient();
                client.ConfigureRequest = r =>
                {
                    r.Headers.Authorization = "Bearer " + token;
                    r.Headers[ProtocolHeaders.Name] = ProtocolHeaders.Version;
                };
                return client.ConnectAsync(new UriBuilder(uri) { Scheme = "http" }.Uri, ct);
            },
        };
        var host = new DesktopHost(platform, dir, _notes, new FileLog(Path.Combine(dir, "logs", "privatepaste.log")),
            time: time, transport: transport);
        if (unlocked) host.SetVaultKey(VaultKey.FromBytes(_vaultKey.Export()));   // as after entering the passphrase
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
        var outcome = await host.SignInAsync(ServerUrl, PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "PC-host");
        Assert.Equal(SignInResult.Ok, outcome.Result);
        await WaitUntilAsync(() => host.State == SyncState.Connected);
        return host;
    }

    [Theory]
    [InlineData("example.com", "Enter a full server address starting with https://")]
    [InlineData("http://example.com", "The server address must use https:// (http:// is only allowed for localhost).")]
    public async Task Invalid_url_returns_the_normalize_message(string url, string message)
    {
        var outcome = await NewHost().SignInAsync(url, "owner", "x", "PC");
        Assert.Equal(new SignInOutcome(SignInResult.InvalidUrl, message), outcome);
    }

    [Fact]
    public async Task Sign_in_remembers_the_name()
    {
        var host = await SignedInHostAsync();
        Assert.Equal(PrivatePasteFactory.OwnerName, host.Settings.Username);
    }

    [Fact]
    public async Task Wrong_password()
    {
        var host = NewHost();
        Assert.Equal(SignInResult.WrongPassword, (await host.SignInAsync(ServerUrl, "owner", "nope", "PC")).Result);
        Assert.False(host.IsSignedIn);
        Assert.False(File.Exists(TokenPath));
    }

    [Fact]
    public async Task Rate_limited_and_unreachable()
    {
        var limited = NewHost(handler: () => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.Equal(SignInResult.RateLimited, (await limited.SignInAsync(ServerUrl, "owner", "x", "PC")).Result);

        var offline = NewHost(handler: () => new StubHandler(_ => throw new HttpRequestException("offline")));
        Assert.Equal(new SignInOutcome(SignInResult.Unreachable, null), await offline.SignInAsync(ServerUrl, "owner", "x", "PC"));
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
        await _server.UploadTextAsync(other, "from phone");
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
        Assert.False(File.Exists(VaultKeyPath));   // the key goes with the sign-in
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
            page = page! with { Items = page.Items.Select(new ClipCodec(_vaultKey).Decode).ToList() };
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
    public async Task An_unreadable_cache_key_is_kept_until_a_sign_in_replaces_it()
    {
        var keyPath = Path.Combine(_dir, "cache.key");
        byte[] foreign = [1, 2, 3, 4, 5];
        File.WriteAllBytes(keyPath, foreign);
        File.WriteAllBytes(Path.Combine(_dir, "cache.dat"), [9, 9, 9]);

        var host = NewHost(new RejectsForeign(foreign));
        Assert.Equal(foreign, File.ReadAllBytes(keyPath));   // never overwritten just by starting
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(_dir, "cache.dat")));
        Assert.Contains("Local history is unavailable this session.", _notes.Messages);

        // Signing in starts a new device identity (cache and queue are cleared anyway): a fresh key leaves ephemeral mode.
        Assert.Equal(SignInResult.Ok, (await host.SignInAsync(ServerUrl, PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "PC")).Result);
        Assert.NotEqual(foreign, File.ReadAllBytes(keyPath));
        await WaitUntilAsync(() => host.State == SyncState.Connected);
        var other = await _server.LoginAsync("Phone");
        await _server.UploadTextAsync(other, "kept now");
        await WaitUntilAsync(() => host.Cache.Items.Any(i => i.Clip.Text == "kept now"));
        await host.DisposeAsync();
        _hosts.Remove(host);

        _notes.Messages.Clear();
        var again = NewHost(new RejectsForeign(foreign));
        Assert.Empty(_notes.Messages);
        Assert.Contains(again.Cache.Items, i => i.Clip.Text == "kept now");
        Assert.True(await again.StartAsync());
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
        Assert.Equal("Connecting…", TrayController.StatusText(true, false, SyncState.Connecting, server));
        Assert.Equal("Paused", TrayController.StatusText(true, true, SyncState.Paused, server));
        Assert.Equal("Paused", TrayController.StatusText(true, true, SyncState.Connected, server));
        Assert.Equal("Signed out", TrayController.StatusText(false, false, SyncState.SignedOut, server));
        Assert.Equal("Signed out", TrayController.StatusText(true, false, SyncState.SignedOut, server));
        // While the vault keeps sync from running, that comes first.
        Assert.Equal("Locked", TrayController.StatusText(true, false, SyncState.Offline, server, VaultState.Locked));
        Assert.Equal("Encryption isn't set up", TrayController.StatusText(true, false, SyncState.Offline, server, VaultState.NeedsOwnerSetup));
        Assert.Equal("Update PrivatePaste to keep syncing.", TrayController.StatusText(true, false, SyncState.Offline, server,
            VaultState.UpdateRequired, VaultCopy.For(VaultState.UpdateRequired, false)));
        Assert.Equal("Signed out", TrayController.StatusText(false, false, SyncState.SignedOut, server, VaultState.Locked));
    }

    // ---------------------------------------------------------------- the vault state machine

    private const string Passphrase = "my cat eats lemons daily";

    private async Task<DesktopHost> SignInAsync(DesktopHost host, string name = PrivatePasteFactory.OwnerName,
        string password = PrivatePasteFactory.Password)
    {
        Assert.Equal(SignInResult.Ok, (await host.SignInAsync(ServerUrl, name, password, "PC-" + Guid.NewGuid().ToString("N")[..6])).Result);
        await WaitUntilAsync(() => host.VaultState != VaultState.Unknown);
        return host;
    }

    private string VaultKeyPath => Path.Combine(_dir, "vault.dat");

    [Fact]
    public async Task Host_PreE2eCacheIsWipedBeforeFirstSync()
    {
        var first = await SignedInHostAsync();
        await first.DisposeAsync();
        _hosts.Remove(first);
        // Today's app wrote these: plain history and a queued copy, with no key id.
        var cacheKey = Convert.FromBase64String(new SecretFile(Path.Combine(_dir, "cache.key"), new XorProtector()).Read()!);
        var old = new global::PrivatePaste.Client.Core.Cache.HistoryCache(Path.Combine(_dir, "cache.dat"), cacheKey);
        old.Upsert(new ClipDto(Guid.NewGuid(), 7, ClipKind.Text, "from before encryption", "h", null, null, 22, null, "PC",
            DateTimeOffset.UtcNow));
        old.KeyId = null;
        old.Save();
        new OutboundQueue(Path.Combine(_dir, "queue.dat"), cacheKey).Enqueue(new ClipboardContent.Text("queued before encryption"),
            DateTimeOffset.UtcNow);
        ForgetFirstContact();
        SwitchHandler? uploads = null;
        var host = NewHost(handler: () => uploads = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey });
        Assert.Contains(host.Cache.Items, i => i.Clip.Text == "from before encryption");

        Assert.True(await host.StartAsync());
        // The stored key goes with the wipe (a pre-encryption app never had one anyway): enter the passphrase again.
        await WaitUntilAsync(() => host.VaultState == VaultState.Locked);

        Assert.Empty(host.Cache.Items);
        Assert.False(File.Exists(VaultKeyPath));
        Assert.Equal(_vaultKey.KeyId, host.Cache.KeyId);
        Assert.Equal(0, OutboundQueue.Load(Path.Combine(_dir, "queue.dat"), cacheKey).Count);
        Assert.Empty(uploads!.Uploads);   // the queued plaintext copy never went out
        Assert.True(NewSettingsStore().Load().MetE2eServer);
    }

    /// <summary>Makes the settings look like those of an app from before encryption (it never met an encrypted server).</summary>
    private void ForgetFirstContact()
    {
        var store = NewSettingsStore();
        store.Save(store.Load() with { MetE2eServer = false });
    }

    [Fact]
    public async Task Host_PreE2eQueueWithoutHistoryIsWipedAndNeverUploaded()
    {
        var first = await SignedInHostAsync();
        await first.DisposeAsync();
        _hosts.Remove(first);
        // An app from before encryption with no history yet, only a copy waiting to be sent (key id null).
        File.Delete(Path.Combine(_dir, "cache.dat"));
        var cacheKey = Convert.FromBase64String(new SecretFile(Path.Combine(_dir, "cache.key"), new XorProtector()).Read()!);
        new OutboundQueue(Path.Combine(_dir, "queue.dat"), cacheKey).Enqueue(new ClipboardContent.Text("plaintext from before"),
            DateTimeOffset.UtcNow);
        ForgetFirstContact();
        SwitchHandler? uploads = null;
        var host = NewHost(handler: () => uploads = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey });
        Assert.Empty(host.Cache.Items);

        Assert.True(await host.StartAsync());
        await WaitUntilAsync(() => host.VaultState == VaultState.Locked);

        Assert.Equal(0, QueuedCount());
        Assert.False(File.Exists(VaultKeyPath));
        Assert.True(NewSettingsStore().Load().MetE2eServer);
        await host.SignOutAsync();
        Assert.Empty(uploads!.Uploads);
        Assert.False(NewSettingsStore().Load().MetE2eServer);   // a sign-out starts the next sign-in from scratch
    }

    /// <summary>Signs in once (so the data folder is set up), then leaves a copy queued the way an app from before encryption did.</summary>
    private async Task PreE2eQueueAsync(string text)
    {
        var first = await SignedInHostAsync();
        await first.DisposeAsync();
        _hosts.Remove(first);
        var cacheKey = Convert.FromBase64String(new SecretFile(Path.Combine(_dir, "cache.key"), new XorProtector()).Read()!);
        new OutboundQueue(Path.Combine(_dir, "queue.dat"), cacheKey).Enqueue(new ClipboardContent.Text(text), DateTimeOffset.UtcNow);
        ForgetFirstContact();
    }

    [Fact]
    public async Task First_contact_clears_a_queue_file_that_could_not_be_read_at_start()
    {
        await PreE2eQueueAsync("plaintext from before");
        SwitchHandler? uploads = null;
        DesktopHost host;
        // Locked while the app starts (a scanner, say): the queue loads empty, but the file is still there.
        using (new FileStream(Path.Combine(_dir, "queue.dat"), FileMode.Open, FileAccess.Read, FileShare.None))
            host = NewHost(handler: () => uploads = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey });

        Assert.True(await host.StartAsync());
        await WaitUntilAsync(() => NewSettingsStore().Load().MetE2eServer && host.State == SyncState.Connected);

        Assert.Equal(0, QueuedCount());     // the file itself was cleared
        await host.DisposeAsync();
        _hosts.Remove(host);
        var again = NewHost(handler: () => uploads = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey });
        Assert.True(await again.StartAsync());
        await WaitUntilAsync(() => again.State == SyncState.Connected);
        Assert.Empty(uploads!.Uploads);     // nothing from before encryption ever went out
    }

    [Fact]
    public async Task A_failed_first_contact_wipe_leaves_the_marker_unset_and_the_next_check_wipes_again()
    {
        await PreE2eQueueAsync("plaintext from before");
        SwitchHandler? uploads = null;
        var host = NewHost(handler: () => uploads = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey });
        host.WipeFault = step => { if (step == "queue") throw new UnauthorizedAccessException(); };

        Assert.True(await host.StartAsync());
        await WaitUntilAsync(() => host.VaultState == VaultState.Locked);   // the key went with the wipe

        Assert.False(NewSettingsStore().Load().MetE2eServer);
        Assert.Equal(1, QueuedCount());     // still on disk
        Assert.False(File.Exists(VaultKeyPath));
        await host.DisposeAsync();
        _hosts.Remove(host);

        var queueWipes = 0;
        var again = NewHost(handler: () => uploads = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey });
        again.WipeFault = step => { if (step == "queue") Interlocked.Increment(ref queueWipes); };
        Assert.True(await again.StartAsync());
        await WaitUntilAsync(() => NewSettingsStore().Load().MetE2eServer);

        Assert.True(Volatile.Read(ref queueWipes) >= 1);
        Assert.Equal(0, QueuedCount());
        Assert.Empty(uploads!.Uploads);
    }

    [Fact]
    public async Task Sign_out_still_wipes_when_stopping_the_session_fails()
    {
        var host = await SignedInHostAsync();
        _clipboard.Current = new ClipboardContent.Text("in the history");
        _clipboard.RaiseChanged();
        await WaitUntilAsync(() => host.Cache.Items.Any(i => i.Clip.Text == "in the history"));
        await WaitUntilAsync(() => File.Exists(Path.Combine(_dir, "cache.dat")));
        var signedOut = 0;
        host.SignedOut += (_, _) => signedOut++;
        host.WipeFault = step => { if (step == "session") throw new InvalidOperationException("boom"); };

        await host.SignOutAsync();

        Assert.Equal(1, signedOut);
        Assert.False(File.Exists(TokenPath));
        Assert.False(File.Exists(VaultKeyPath));
        Assert.False(File.Exists(Path.Combine(_dir, "cache.dat")));
        Assert.Empty(host.Cache.Items);
        Assert.Null(host.Codec);
        Assert.Equal(VaultState.Unknown, host.VaultState);
        Assert.False(NewSettingsStore().Load().MetE2eServer);
    }

    [Fact]
    public async Task Host_OldServerShowsServerTooOld()
    {
        var host = NewHost(handler: () => new OldServer(_server.Server.CreateHandler()));
        await SignInAsync(host);

        Assert.Equal(VaultState.ServerTooOld, host.VaultState);
        Assert.Equal("Update your PrivatePaste server to keep syncing.", host.VaultMessage);
        Assert.Null(host.Codec);
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref _connects));   // sync never starts
    }

    [Fact]
    public async Task Host_NonOwnerWithoutVaultWaits()
    {
        using var server = new PrivatePasteFactory();
        await server.AddPersonAsync("ana", "ana-password-1");
        var ana = await SignInAsync(NewHost(server: server, unlocked: false), "ana", "ana-password-1");
        Assert.Equal(VaultState.WaitingForOwner, ana.VaultState);
        Assert.Equal("Ask the owner to set up encryption.", ana.VaultMessage);
        Assert.False(ana.IsOwner);
        Assert.Equal(VaultCopy.WaitingForOwner, await ana.SetUpVaultAsync(Passphrase, Passphrase));   // 403

        var owner = await SignInAsync(NewHost(server: server, dir: Path.Combine(_dir, "owner"), unlocked: false));
        Assert.Equal(VaultState.NeedsOwnerSetup, owner.VaultState);
        Assert.True(owner.IsOwner);
        Assert.Null(owner.VaultMessage);
    }

    [Fact]
    public async Task Setup_then_another_computer_unlocks_with_the_passphrase()
    {
        using var server = new PrivatePasteFactory();
        var owner = await SignInAsync(NewHost(server: server, unlocked: false));
        Assert.Equal(VaultState.NeedsOwnerSetup, owner.VaultState);
        Assert.Equal(PassphraseWrap.RuleMessage, await owner.SetUpVaultAsync("short", "short"));
        Assert.Equal(VaultCopy.Mismatch, await owner.SetUpVaultAsync(Passphrase, Passphrase + "!"));

        Assert.Null(await owner.SetUpVaultAsync(Passphrase, Passphrase));

        Assert.Equal(VaultState.Unlocked, owner.VaultState);
        Assert.True(File.Exists(VaultKeyPath));
        await WaitUntilAsync(() => owner.State == SyncState.Connected);
        _clipboard.Current = new ClipboardContent.Text("sealed here");
        _clipboard.RaiseChanged();
        await WaitUntilAsync(() => owner.Cache.Items.Any(i => i.Clip.Text == "sealed here"));

        // Setting up again from elsewhere: already done.
        var late = await SignInAsync(NewHost(server: server, dir: Path.Combine(_dir, "late"), unlocked: false));
        Assert.Equal(VaultState.Locked, late.VaultState);
        Assert.Null(late.Codec);
        Assert.Empty(late.Cache.Items);
        Assert.Equal(VaultCopy.WrongPassphrase, await late.UnlockAsync("not the passphrase at all"));
        Assert.Null(await late.UnlockAsync(Passphrase));
        Assert.Equal(VaultState.Unlocked, late.VaultState);
        await WaitUntilAsync(() => late.Cache.Items.Any(i => i.Clip.Text == "sealed here"));
    }

    [Fact]
    public async Task A_second_setup_gets_the_set_up_elsewhere_copy_and_locks()
    {
        using var server = new PrivatePasteFactory();
        var a = await SignInAsync(NewHost(server: server, unlocked: false));
        var b = await SignInAsync(NewHost(server: server, dir: Path.Combine(_dir, "b"), unlocked: false));
        Assert.Null(await a.SetUpVaultAsync(Passphrase, Passphrase));

        Assert.Equal(VaultCopy.SetUpElsewhere, await b.SetUpVaultAsync("another long passphrase", "another long passphrase"));
        await WaitUntilAsync(() => b.VaultState == VaultState.Locked);   // the check loop settles it
    }

    [Fact]
    public async Task Unlock_WrongPassphraseShowsCopy()
    {
        using var server = new PrivatePasteFactory();
        var owner = await SignInAsync(NewHost(server: server, unlocked: false));
        Assert.Null(await owner.SetUpVaultAsync(Passphrase, Passphrase));
        var other = await SignInAsync(NewHost(server: server, dir: Path.Combine(_dir, "other"), unlocked: false));

        Assert.Equal("That passphrase doesn't unlock this server's history.", await other.UnlockAsync("my dog eats lemons daily"));

        Assert.Equal(VaultState.Locked, other.VaultState);
        Assert.False(File.Exists(Path.Combine(_dir, "other", "vault.dat")));
    }

    [Fact]
    public async Task Unlock_of_an_unsupported_wrap_says_to_update()
    {
        using var server = new PrivatePasteFactory();
        using var key = VaultKey.Create();
        var wrap = PrivatePasteFactory.RandomWrap(key.KeyId);
        // Stored straight in the database: a wrap from a newer app the server lets through, this client doesn't accept.
        Assert.True(await server.Services.GetRequiredService<VaultRepository>()
            .CreateAsync(wrap with { Kdf = wrap.Kdf with { Alg = "argon3" } }));
        var host = await SignInAsync(NewHost(server: server, unlocked: false));
        Assert.Equal(VaultState.Locked, host.VaultState);

        Assert.Equal("This server's encryption settings aren't supported. Update PrivatePaste.", await host.UnlockAsync(Passphrase));
    }

    [Fact]
    public async Task Settings_ChangePassphraseOwnerOnly()
    {
        using var server = new PrivatePasteFactory();
        await server.AddPersonAsync("ana", "ana-password-1");
        var owner = await SignInAsync(NewHost(server: server, unlocked: false));
        Assert.Null(await owner.SetUpVaultAsync(Passphrase, Passphrase));
        var ana = await SignInAsync(NewHost(server: server, dir: Path.Combine(_dir, "ana"), unlocked: false), "ana", "ana-password-1");
        Assert.Null(await ana.UnlockAsync(Passphrase));
        Assert.False(ana.IsOwner);

        const string next = "a new passphrase for everyone";
        Assert.Equal(VaultCopy.OwnerOnly, await ana.ChangePassphraseAsync(Passphrase, next, next));
        Assert.Equal("The current passphrase is wrong.", await owner.ChangePassphraseAsync("not the passphrase at all", next, next));
        Assert.Equal(VaultCopy.Mismatch, await owner.ChangePassphraseAsync(Passphrase, next, next + "!"));
        Assert.Null(await owner.ChangePassphraseAsync(Passphrase, next, next));

        // The key stays the same: a new computer unlocks with the new passphrase only.
        var fresh = await SignInAsync(NewHost(server: server, dir: Path.Combine(_dir, "fresh"), unlocked: false));
        Assert.Equal(VaultCopy.WrongPassphrase, await fresh.UnlockAsync(Passphrase));
        Assert.Null(await fresh.UnlockAsync(next));
        Assert.Equal(VaultState.Unlocked, owner.VaultState);
    }

    [Fact]
    public async Task Add_a_device_hands_the_key_over_once()
    {
        var host = await SignedInHostAsync();

        var (created, qr) = await host.CreateHandoverAsync();

        Assert.True(Handover.TryParseQr(qr, out var id, out var otk, out var keyId));
        Assert.Equal(created.Id, id);
        Assert.Equal(_vaultKey.KeyId, keyId);
        var blob = (await (await _server.LoginAsync("Phone2")).GetFromJsonAsync<HandoverBlob>($"/api/vault/handovers/{id}",
            ProtocolJson.Options))!;
        using var key = Handover.Unwrap(otk, Convert.FromBase64String(blob.Blob), keyId);
        Assert.Equal(_vaultKey.Export(), key.Export());
    }

    [Fact]
    public async Task SignOut_DeletesVaultKey()
    {
        var host = await SignedInHostAsync();
        Assert.True(File.Exists(VaultKeyPath));
        Assert.Equal(VaultState.Unlocked, host.VaultState);
        Assert.NotNull(host.Codec);

        await host.SignOutAsync();

        Assert.False(File.Exists(VaultKeyPath));
        Assert.Null(host.Codec);
        Assert.Equal(VaultState.Unknown, host.VaultState);
    }

    [Fact]
    public async Task A_key_reset_message_wipes_history_queue_and_key_and_locks()
    {
        SwitchHandler? sw = null;
        var host = NewHost(handler: () => sw = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey });
        await SignInAsync(host);
        await WaitUntilAsync(() => host.State == SyncState.Connected);
        var phone = await _server.LoginAsync("Phone");
        await _server.UploadTextAsync(phone, "history");
        await WaitUntilAsync(() => host.Cache.Items.Any(i => i.Clip.Text == "history"));
        sw!.Statuses.Enqueue(503);   // a live copy that has to wait in the queue
        _clipboard.Current = new ClipboardContent.Text("waiting");
        _clipboard.RaiseChanged();
        await WaitUntilAsync(() => File.Exists(Path.Combine(_dir, "queue.dat")) && QueuedCount() == 1);
        var states = new System.Collections.Concurrent.ConcurrentQueue<VaultState>();
        host.VaultStateChanged += (_, s) => states.Enqueue(s);

        using var newKey = VaultKey.Create();
        (await phone.PostAsJsonAsync("/api/vault/reset", PrivatePasteFactory.RandomWrap(newKey.KeyId), ProtocolJson.Options))
            .EnsureSuccessStatusCode();   // the server sends vault.reset

        await WaitUntilAsync(() => host.VaultState == VaultState.Locked);
        Assert.Equal([VaultState.Locked], states);
        Assert.True(host.KeyWasReset);
        Assert.Equal("Encryption was reset. Enter the new passphrase.", host.VaultMessage);
        Assert.Empty(host.Cache.Items);
        Assert.Equal(0, QueuedCount());
        Assert.False(File.Exists(VaultKeyPath));
        Assert.Null(host.Codec);
        Assert.True(host.IsSignedIn);   // the sign-in stays
    }

    [Fact]
    public async Task A_key_reset_raised_from_a_flush_does_not_deadlock()
    {
        var offline = false;
        SwitchHandler? sw = null;
        var host = NewHost(handler: () => sw = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey },
            wsOffline: () => Volatile.Read(ref offline));
        await SignInAsync(host);
        await WaitUntilAsync(() => host.State == SyncState.Connected);

        // Offline: the link drops and a copy waits in the queue.
        Volatile.Write(ref offline, true);
        sw!.Offline = true;
        host.OnNetworkOrPowerChanged("test");
        await WaitUntilAsync(() => host.State == SyncState.Offline);
        _clipboard.Current = new ClipboardContent.Text("copied offline");
        _clipboard.RaiseChanged();
        await WaitUntilAsync(() => QueuedCount() == 1);
        using var newKey = VaultKey.Create();
        var owner = await _server.LoginAsync("Web");
        (await owner.PostAsJsonAsync("/api/vault/reset", PrivatePasteFactory.RandomWrap(newKey.KeyId), ProtocolJson.Options))
            .EnsureSuccessStatusCode();   // this computer misses the message

        // Back online: connect, catch up, flush; the flush's upload gets the 409 under the coordinator's gate.
        sw.Offline = false;
        Volatile.Write(ref offline, false);

        await WaitUntilAsync(() => host.VaultState == VaultState.Locked);
        Assert.Equal(0, QueuedCount());
        Assert.Contains("copied offline", sw.Uploads);
        Assert.Empty(host.Cache.Items);
        Assert.False(File.Exists(VaultKeyPath));
        // The host still works: signing out goes through.
        await host.SignOutAsync().WaitAsync(WaitTimeout);
    }

    private int QueuedCount(string? dir = null)
    {
        dir ??= _dir;
        var key = Convert.FromBase64String(new SecretFile(Path.Combine(dir, "cache.key"), new XorProtector()).Read()!);
        return OutboundQueue.Load(Path.Combine(dir, "queue.dat"), key).Count;
    }

    [Fact]
    public async Task Update_required_from_the_engine_settles_the_state()
    {
        var old = false;
        var host = NewHost(handler: () => new SwitchHandler(_server.Server.CreateHandler())
        {
            Respond = r => Volatile.Read(ref old) && r.RequestUri!.AbsolutePath == "/api/sync"
                ? new HttpResponseMessage(HttpStatusCode.UpgradeRequired) : null,
        });
        await SignInAsync(host);
        await WaitUntilAsync(() => host.State == SyncState.Connected);
        Volatile.Write(ref old, true);

        host.OnNetworkOrPowerChanged("test");   // reconnect: the catch-up gets the 426

        await WaitUntilAsync(() => host.VaultState == VaultState.UpdateRequired);
        Assert.Equal("Update PrivatePaste to keep syncing.", host.VaultMessage);
    }

    [Fact]
    public async Task A_wipe_that_cannot_clear_the_history_still_locks_and_drops_the_key()
    {
        var host = await SignedInHostAsync();
        Assert.True(File.Exists(VaultKeyPath));
        // The history file can't be removed (no access): the other steps and the lock must happen anyway.
        var faults = 0;
        host.WipeFault = step =>
        {
            if (step != "history") return;
            Interlocked.Increment(ref faults);
            throw new UnauthorizedAccessException("denied");
        };
        var owner = await _server.LoginAsync("Web");
        using var newKey = VaultKey.Create();

        (await owner.PostAsJsonAsync("/api/vault/reset", PrivatePasteFactory.RandomWrap(newKey.KeyId), ProtocolJson.Options))
            .EnsureSuccessStatusCode();

        await WaitUntilAsync(() => host.VaultState == VaultState.Locked);
        Assert.False(File.Exists(VaultKeyPath));
        Assert.Null(host.Codec);
        Assert.True(host.KeyWasReset);
        Assert.Equal(1, Volatile.Read(ref faults));
        // The log names the type only, never the exception's text. (The line itself isn't asserted: the log drops a write
        // it can't make, e.g. while a scanner holds the file.)
        Assert.DoesNotContain("denied", File.ReadAllText(Path.Combine(_dir, "logs", "privatepaste.log")));
    }

    [Fact]
    public async Task Copies_made_while_locked_after_a_reset_go_out_under_the_new_key()
    {
        SwitchHandler? sw = null;
        var host = NewHost(handler: () => sw = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey });
        await SignInAsync(host);
        await WaitUntilAsync(() => host.State == SyncState.Connected);
        using var newKey = VaultKey.Create();
        const string next = "the passphrase after the reset";
        var owner = await _server.LoginAsync("Web");
        (await owner.PostAsJsonAsync("/api/vault/reset", PassphraseWrap.Wrap(newKey, next), ProtocolJson.Options))
            .EnsureSuccessStatusCode();
        await WaitUntilAsync(() => host.VaultState == VaultState.Locked && host.Cache.KeyId == newKey.KeyId);   // claimed

        _clipboard.Current = new ClipboardContent.Text("copied while locked");
        _clipboard.RaiseChanged();
        await WaitUntilAsync(() => QueuedCount() == 1);
        sw!.Key = newKey;

        Assert.Null(await host.UnlockAsync(next));

        await WaitUntilAsync(() => sw.Uploads.Contains("copied while locked"));
        Assert.Equal(newKey.KeyId, sw.UploadKeyIds[sw.Uploads.IndexOf("copied while locked")]);
        await WaitUntilAsync(() => QueuedCount() == 0);
    }

    [Fact]
    public async Task Copies_made_while_waiting_for_the_owner_survive_a_recheck()
    {
        using var server = new PrivatePasteFactory();
        await server.AddPersonAsync("ana", "ana-password-1");
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var infoAsks = 0;
        var dir = Path.Combine(_dir, "ana");
        var ana = NewHost(server: server, dir: dir, unlocked: false, time: time,
            handler: () => new SwitchHandler(server.Server.CreateHandler())
            {
                Respond = r =>
                {
                    if (r.RequestUri!.AbsolutePath == "/api/server-info") Interlocked.Increment(ref infoAsks);
                    return null;
                },
            });
        await SignInAsync(ana, "ana", "ana-password-1");
        Assert.Equal(VaultState.WaitingForOwner, ana.VaultState);
        _clipboard.Current = new ClipboardContent.Text("copied while waiting");
        _clipboard.RaiseChanged();
        await WaitUntilAsync(() => QueuedCount(dir) == 1);
        var coordinator = ana.Coordinator;
        var asked = Volatile.Read(ref infoAsks);

        await WaitUntilAsync(() =>
        {
            time.Advance(DesktopHost.RecheckInterval);
            return Volatile.Read(ref infoAsks) > asked + 1;   // asked again, twice
        });

        Assert.Equal(VaultState.WaitingForOwner, ana.VaultState);
        Assert.Equal(1, QueuedCount(dir));
        Assert.Same(coordinator, ana.Coordinator);   // nothing was wiped or rebuilt

        // The owner sets up; ana's next recheck finds the vault, and after the unlock the copy goes out under its key.
        var owner = await SignInAsync(NewHost(server: server, dir: Path.Combine(_dir, "owner"), unlocked: false));
        Assert.Null(await owner.SetUpVaultAsync(Passphrase, Passphrase));
        await WaitUntilAsync(() =>
        {
            time.Advance(DesktopHost.RecheckInterval);
            return ana.VaultState == VaultState.Locked;
        });
        Assert.Equal(1, QueuedCount(dir));
        Assert.Null(await ana.UnlockAsync(Passphrase));
        await WaitUntilAsync(() => ana.Cache.Items.Any(i => i.Clip.Text == "copied while waiting"));
        await WaitUntilAsync(() => QueuedCount(dir) == 0);
        await WaitUntilAsync(() => owner.Cache.Items.Any(i => i.Clip.Text == "copied while waiting"));
    }

    /// <summary>A server from before end-to-end encryption: no /api/server-info.</summary>
    private sealed class OldServer(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.AbsolutePath == "/api/server-info"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : base.SendAsync(request, ct);
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
            await _server.UploadTextAsync(other, text);
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
        Assert.Equal(SignInResult.Ok, (await host.SignInAsync(ServerUrl, PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "PC-host")).Result);
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

    [Fact]
    public async Task Blocked_apps_are_saved_and_applied_at_once()
    {
        var host = await SignedInHostAsync();
        Assert.Empty(host.Settings.BlockedApps);
        host.SetBlockedApps(["keepassxc", "chrome"]);
        Assert.Equal(["keepassxc", "chrome"], host.Settings.BlockedApps);
        Assert.Equal(["keepassxc", "chrome"], NewSettingsStore().Load().BlockedApps);   // persisted
    }

    [Fact]
    public async Task Clip_arrived_is_raised_for_a_live_clip_from_another_device_unless_turned_off()
    {
        var host = await SignedInHostAsync();
        Assert.True(host.Settings.NotifyOnClipArrival);   // on by default
        var arrived = new System.Collections.Concurrent.ConcurrentQueue<ClipDto>();
        host.ClipArrived += (_, c) => arrived.Enqueue(c);
        var phone = await _server.LoginAsync("Phone", "android");

        async Task UploadAsync(string text)
        {
            await _server.UploadTextAsync(phone, text);
            await WaitUntilAsync(() => _clipboard.Writes.OfType<ClipboardContent.Text>().Any(w => w.Value == text));
        }

        await UploadAsync("one");
        await WaitUntilAsync(() => !arrived.IsEmpty);
        var first = Assert.Single(arrived);
        Assert.Equal("one", first.Text);
        Assert.Equal("Phone", first.SourceDeviceName);
        Assert.Equal("android", first.SourceDevicePlatform);

        // The user's own pick from the popup writes the clipboard too, but is not an arrival.
        Assert.True(await host.Coordinator!.ApplyRemoteAsync(first with { Hash = "picked-again" }, CancellationToken.None));

        host.SetNotifyOnClipArrival(false);
        Assert.False(NewSettingsStore().Load().NotifyOnClipArrival);   // persisted
        await UploadAsync("two");
        await Task.Delay(100);
        Assert.Single(arrived);

        host.SetNotifyOnClipArrival(true);
        await UploadAsync("three");
        await WaitUntilAsync(() => arrived.Count == 2);
        Assert.Equal(["one", "three"], arrived.Select(c => c.Text));
    }

    // ---- File clips ----

    private string FilesRoot => Path.Combine(_dir, "files");

    /// <summary>Another device sends <paramref name="name"/>; returns the clip once this host put it on the clipboard.</summary>
    private async Task<ClipDto> ReceiveFileAsync(DesktopHost host, string name, string content)
    {
        var source = Path.Combine(_dir, "phone", name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, content);
        var api = new PrivatePasteApi(new HttpClient(_server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") });
        api.Token = (await api.LoginAsync(PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "Phone", "android")).Token;
        var codec = new ClipCodec(VaultKey.FromBytes(_vaultKey.Export()));
        var stored = codec.Decode(await api.UploadClipAsync(
            await codec.EncodeFilesAsync([await PrivatePaste.Client.Core.Files.LocalFile.ReadAsync(source, default)], null, default)));
        await WaitUntilAsync(() => _clipboard.Current is ClipboardContent.Files);
        return host.Cache.Items.Single(i => i.Clip.Id == stored.Id).Clip;
    }

    [Fact]
    public async Task Received_files_are_kept_while_in_the_history_and_cleared_on_sign_out()
    {
        var host = await SignedInHostAsync();
        var clip = await ReceiveFileAsync(host, "report.pdf", "pdf bytes");
        var written = Assert.Single(((ClipboardContent.Files)_clipboard.Current!).Paths);
        Assert.Equal(Path.Combine(FilesRoot, clip.Id.ToString("N"), "report.pdf"), written);
        await host.DisposeAsync();
        _hosts.Remove(host);

        // A restart prunes what the history no longer holds, and keeps the rest.
        var stray = Path.Combine(FilesRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "old.txt"), "gone");
        var again = NewHost();
        Assert.False(Directory.Exists(stray));
        Assert.True(File.Exists(written));

        Assert.True(await again.StartAsync());
        await WaitUntilAsync(() => again.State == SyncState.Connected);
        await again.SignOutAsync();
        Assert.False(Directory.Exists(FilesRoot));
    }

    [Fact]
    public async Task A_key_reset_clears_the_received_files()
    {
        var host = await SignedInHostAsync();
        await ReceiveFileAsync(host, "report.pdf", "pdf bytes");
        Assert.True(Directory.Exists(FilesRoot));

        var phone = await _server.LoginAsync("Web");
        using var newKey = VaultKey.Create();
        (await phone.PostAsJsonAsync("/api/vault/reset", PrivatePasteFactory.RandomWrap(newKey.KeyId), ProtocolJson.Options))
            .EnsureSuccessStatusCode();   // the server sends vault.reset

        await WaitUntilAsync(() => host.VaultState == VaultState.Locked);
        Assert.False(Directory.Exists(FilesRoot));
    }

    [Fact]
    public async Task Save_copies_the_files_into_downloads_and_says_where()
    {
        var host = await SignedInHostAsync();
        var downloads = Path.Combine(_dir, "Downloads");
        host.DownloadsOverride = downloads;
        var clip = await ReceiveFileAsync(host, "report.pdf", "pdf bytes");
        Directory.Delete(Path.Combine(FilesRoot, clip.Id.ToString("N")), recursive: true);   // gone: Save downloads it again

        Assert.Equal("Saved report.pdf to Downloads", await host.SaveFilesAsync(clip));
        Assert.Equal("pdf bytes", File.ReadAllText(Path.Combine(downloads, "report.pdf")));
        Assert.Equal("Saved report (2).pdf to Downloads", await host.SaveFilesAsync(clip));

        var text = clip with { Kind = ClipKind.Text, Text = "x", Files = null };
        Assert.Null(await host.SaveFilesAsync(text));   // not a file clip: nothing to say
    }

    [Fact]
    public async Task Save_that_cannot_write_names_no_file()
    {
        var host = await SignedInHostAsync();
        var blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "x");
        host.DownloadsOverride = Path.Combine(blocker, "Downloads");   // a file where the folder should be
        var clip = await ReceiveFileAsync(host, "secret-name.pdf", "pdf bytes");

        Assert.Equal(PopupViewModel.CouldNotSaveText, await host.SaveFilesAsync(clip));
        var log = File.ReadAllText(Path.Combine(_dir, "logs", "privatepaste.log"));
        Assert.DoesNotContain("secret-name", log);
    }

    [Fact]
    public async Task A_save_in_flight_survives_a_prune()
    {
        var blobs = new BlobGate();
        var host = NewHost(handler: () => new HoldBlobs(_server.Server.CreateHandler(), blobs));
        Assert.Equal(SignInResult.Ok, (await host.SignInAsync(ServerUrl, PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "PC-host")).Result);
        await WaitUntilAsync(() => host.State == SyncState.Connected);
        host.DownloadsOverride = Path.Combine(_dir, "Downloads");
        var clip = await ReceiveFileAsync(host, "report.pdf", "pdf bytes");
        _clipboard.Current = null;
        await ReceiveFileAsync(host, "other.txt", "other");   // now on the clipboard instead
        Directory.Delete(Path.Combine(FilesRoot, clip.Id.ToString("N")), recursive: true);   // Save downloads it again

        blobs.Armed = true;
        var save = host.SaveFilesAsync(clip);
        await blobs.Entered.Task.WaitAsync(WaitTimeout);
        var pruned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.HistoryChanged += (_, _) => pruned.TrySetResult();
        // The clip leaves the history (like an older search hit, never in it): the next cache change prunes.
        Assert.True(host.Cache.Remove(clip.Id, clip.Seq + 1));
        await pruned.Task.WaitAsync(WaitTimeout);
        blobs.Release.SetResult();

        Assert.Equal("Saved report.pdf to Downloads", await save);
        Assert.Equal("pdf bytes", File.ReadAllText(Path.Combine(_dir, "Downloads", "report.pdf")));
    }

    [Fact]
    public async Task A_save_that_finishes_after_a_sign_out_leaves_no_files()
    {
        var blobs = new BlobGate();
        var host = NewHost(handler: () => new HoldBlobs(_server.Server.CreateHandler(), blobs));
        Assert.Equal(SignInResult.Ok, (await host.SignInAsync(ServerUrl, PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "PC-host")).Result);
        await WaitUntilAsync(() => host.State == SyncState.Connected);
        host.DownloadsOverride = Path.Combine(_dir, "Downloads");
        var clip = await ReceiveFileAsync(host, "report.pdf", "pdf bytes");
        var folder = Path.Combine(FilesRoot, clip.Id.ToString("N"));
        Directory.Delete(folder, recursive: true);

        blobs.Armed = true;
        var save = host.SaveFilesAsync(clip);
        await blobs.Entered.Task.WaitAsync(WaitTimeout);
        await host.SignOutAsync();
        blobs.Release.SetResult();
        await save.WaitAsync(WaitTimeout);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_clip_deleted_on_the_server_takes_its_received_files_with_it()
    {
        var host = await SignedInHostAsync();
        var clip = await ReceiveFileAsync(host, "report.pdf", "pdf bytes");
        var folder = Path.Combine(FilesRoot, clip.Id.ToString("N"));
        Assert.True(Directory.Exists(folder));

        var web = await _server.LoginAsync("Web");
        (await web.DeleteAsync($"/api/clips/{clip.Id}")).EnsureSuccessStatusCode();

        await WaitUntilAsync(() => !host.Cache.Items.Any(i => i.Clip.Id == clip.Id) && !Directory.Exists(folder));
    }


    private sealed class BlockedUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }   // never pumped while the thread is blocked
        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException();
    }

    /// <summary>Holds clip uploads until released (honouring cancellation late), after signalling that one arrived.</summary>
    private sealed class HoldUploads(HttpMessageHandler inner, TaskCompletionSource entered, Task release) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (SwitchHandler.IsUpload(request))
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

    private sealed class BlobGate
    {
        public volatile bool Armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Once armed, holds blob downloads until released, after signalling that one arrived.</summary>
    private sealed class HoldBlobs(HttpMessageHandler inner, BlobGate gate) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (gate.Armed && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/blobs/"))
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task;   // ignores cancellation: the response may come after a sign-out
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

    /// <summary>Works like <see cref="XorProtector"/> but cannot unlock one particular blob (e.g. another user's key).</summary>
    private sealed class RejectsForeign(byte[] foreign) : ISecretProtector
    {
        private readonly XorProtector _inner = new();
        public byte[] Protect(byte[] plaintext) => _inner.Protect(plaintext);
        public byte[] Unprotect(byte[] protectedData) => protectedData.AsSpan().SequenceEqual(foreign)
            ? throw new System.Security.Cryptography.CryptographicException("not yours")
            : _inner.Unprotect(protectedData);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
