using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Security.Cryptography;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Client.Core.Settings;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Desktop.Input;
using PrivatePaste.Desktop.Logging;
using PrivatePaste.Desktop.Platform;
using PrivatePaste.Desktop.ViewModels;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop;

/// <summary>
/// Composition root: owns settings, secrets, the local cache and offline queue, and, while signed in, one
/// <see cref="Session"/> (HTTP client, API, coordinator, sync engine). UI-free; events are raised on background
/// threads, so UI subscribers must marshal to the UI thread.
/// </summary>
public sealed class DesktopHost : IAsyncDisposable
{
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(5);

    private readonly INotifier _notifier;
    private readonly FileLog _log;
    private readonly TimeProvider _time;
    private readonly HostTransport _transport;
    private readonly SettingsStore _settingsStore;
    private readonly SecretFile _tokenFile;
    // Replaced once when a sign-in leaves ephemeral mode (see LeaveEphemeralMode); otherwise fixed.
    private byte[] _cacheKey;
    private string _cachePath;
    private string _queuePath;
    private string? _ephemeralDirectory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private ClientSettings _settings;
    private OutboundQueue _queue;
    private volatile Session? _session;
    private volatile bool _paused;

    public DesktopHost(
        PlatformServices platform, string dataDirectory, INotifier notifier, FileLog log,
        TimeProvider? time = null, HostTransport? transport = null)
    {
        _transport = transport ?? HostTransport.Default;
        Platform = platform;
        DataDirectory = dataDirectory;
        _notifier = notifier;
        _log = log;
        _time = time ?? TimeProvider.System;

        // ---- Settings ----
        _settingsStore = new SettingsStore(dataDirectory);
        _settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(_settings.DeviceName))
            _settings = _settings with { DeviceName = Environment.MachineName };
        _paused = _settings.Paused;

        // ---- Secrets, cache and queue ----
        _tokenFile = new SecretFile(Path.Combine(dataDirectory, "token.dat"), platform.SecretProtector);
        if (LoadOrCreateCacheKey() is { } key)
        {
            _cacheKey = key;
            _cachePath = Path.Combine(dataDirectory, "cache.dat");
            _queuePath = Path.Combine(dataDirectory, "queue.dat");
        }
        else
        {
            // The key file exists but cannot be unlocked right now (locked, or another user's data): never overwrite it.
            // Run this session on a throwaway key in a temp folder so nothing on disk is lost or readable later.
            _cacheKey = RandomNumberGenerator.GetBytes(32);
            _ephemeralDirectory = Path.Combine(Path.GetTempPath(), "PrivatePaste-" + Guid.NewGuid().ToString("N"));
            _cachePath = Path.Combine(_ephemeralDirectory, "cache.dat");
            _queuePath = Path.Combine(_ephemeralDirectory, "queue.dat");
            _notifier.Notify("Local history is unavailable this session.");
        }
        Cache = HistoryCache.Load(_cachePath, _cacheKey);
        Cache.Changed += OnCacheChanged;
        _queue = OutboundQueue.Load(_queuePath, _cacheKey);

        // ---- Clipboard, network and power ----
        platform.Clipboard.Changed += OnClipboardChanged;
        WatchNetworkAndPower();
    }

    public PlatformServices Platform { get; }
    public string DataDirectory { get; }
    /// <summary>The local history. A different instance only after a sign-in that left ephemeral mode.</summary>
    public HistoryCache Cache { get; private set; }

    /// <summary>A snapshot of the persisted settings.</summary>
    public ClientSettings Settings => _settings;

    /// <summary>The API of the current session; null while signed out.</summary>
    public PrivatePasteApi? Api => _session?.Api;

    /// <summary>The coordinator of the current session; null while signed out.</summary>
    public ClipboardCoordinator? Coordinator => _session?.Coordinator;

    public bool IsSignedIn => _session is not null;

    public SyncState State => _session?.Engine.State ?? SyncState.SignedOut;

    /// <summary>The server of the current session (or the last one used); null if never signed in.</summary>
    public Uri? ServerUri => _settings.ServerUrl is { } s && Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;

    /// <summary>Persisted. Setting false runs the resume sequence (see <see cref="SetPausedAsync"/>) in the background.</summary>
    public bool Paused
    {
        get => _paused;
        set => _ = SetPausedAsync(value);
    }

    /// <summary>The local history changed (coalesced; raised on the thread pool). See <see cref="OnCacheChanged"/>.</summary>
    public event EventHandler? HistoryChanged;
    public event EventHandler<SyncState>? StateChanged;
    public event EventHandler? PausedChanged;
    /// <summary>Raised after any sign-out: explicit, revoked by the server, or a 401.</summary>
    public event EventHandler? SignedOut;
    /// <summary>A new hotkey was saved (Task 9 re-registers the global hook).</summary>
    public event EventHandler<HotkeyGesture>? HotkeyChanged;
    /// <summary>
    /// A clip from another device was just written to the clipboard by sync (see <see cref="SyncEngine.ClipApplied"/>) and
    /// <see cref="ClientSettings.NotifyOnClipArrival"/> is on. Raised on the sync engine's loop: handlers must be quick and
    /// marshal to the UI thread. Never raised for popup picks or restores (those do not go through the engine).
    /// </summary>
    public event EventHandler<ClipDto>? ClipArrived;

    // ======================================================================== Startup

    /// <summary>Resumes the stored session if there is one. Returns false when the user must sign in.</summary>
    public async Task<bool> StartAsync()
    {
        ApplyAutostart();
        await _lifecycle.WaitAsync();
        try
        {
            if (_session is not null) return true;
            var token = _tokenFile.Read();
            if (token is null || _settings.DeviceId is not { } deviceId || ServerUri is not { } server) return false;
            StartSession(server, token, deviceId);
            _log.Info("Resumed the stored session.");
            return true;
        }
        finally { _lifecycle.Release(); }
    }

    // ======================================================================== Sign-in / sign-out

    public async Task<SignInOutcome> SignInAsync(string serverUrl, string password, string deviceName)
    {
        Uri server;
        try { server = ServerUrl.Normalize(serverUrl); }
        catch (ArgumentException e) { return new SignInOutcome(SignInResult.InvalidUrl, e.Message); }

        deviceName = deviceName.Trim();
        if (deviceName.Length > 50) deviceName = deviceName[..50];

        await _lifecycle.WaitAsync();
        try
        {
            if (_session is not null) await EndSessionAsync(logout: true);

            var http = CreateHttpClient(server);
            LoginResponse login;
            try
            {
                login = await new PrivatePasteApi(http, _time).LoginAsync(password, deviceName, Platform.PlatformName, _shutdown.Token);
            }
            catch (Exception e)
            {
                http.Dispose();
                return e switch
                {
                    UnauthorizedApiException => new SignInOutcome(SignInResult.WrongPassword, null),
                    ApiException { Status: HttpStatusCode.TooManyRequests } => new SignInOutcome(SignInResult.RateLimited, null),
                    _ => LogUnreachable(e),
                };
            }

            // A new device identity: history and unsent clips of any earlier sign-in do not carry over.
            LeaveEphemeralMode();
            Cache.Clear();
            ClearQueue();
            try { _tokenFile.Write(login.Token); }
            catch (Exception e)
            {
                _log.Error("Could not store the sign-in token.", e);
                _notifier.Notify("Signed in, but the sign-in could not be saved for the next start.");
            }
            UpdateSettings(s => s with { ServerUrl = server.AbsoluteUri, DeviceName = deviceName, DeviceId = login.DeviceId });
            StartSession(server, login.Token, login.DeviceId, http);
            _log.Info("Signed in.");
            return new SignInOutcome(SignInResult.Ok, null);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Best-effort logout on the server, then deletes the token and clears the cache and queue.</summary>
    public async Task SignOutAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_session is null) return;
            await EndSessionAsync(logout: true);
        }
        finally { _lifecycle.Release(); }
        _log.Info("Signed out.");
        RaiseSignedOut();
    }

    /// <summary>The server revoked this device (1008 close or a 401): sign out locally without calling the server.</summary>
    private async Task OnRevokedAsync(Session session)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_session != session) return;   // already signed out or replaced
            await EndSessionAsync(logout: false);
        }
        finally { _lifecycle.Release(); }
        _log.Info("This device was signed out by the server.");
        _notifier.Notify("This device was signed out.");
        RaiseSignedOut();
    }

    // Caller holds _lifecycle.
    private async Task EndSessionAsync(bool logout)
    {
        var session = _session!;
        _session = null;
        await session.CloseAsync();   // cancel and drain clipboard work first: nothing may write the cache/queue after the clear
        await session.Engine.StopAsync();
        if (logout)
        {
            using var timeout = new CancellationTokenSource(LogoutTimeout);
            try { await session.Api.LogoutAsync(timeout.Token); }
            catch (Exception e) { _log.Error("Logout request failed (signed out locally anyway).", e); }
        }
        session.Http.Dispose();
        _tokenFile.Delete();
        Cache.Clear();
        ClearQueue();
        UpdateSettings(s => s with { DeviceId = null });
    }

    private void RaiseSignedOut()
    {
        StateChanged?.Invoke(this, SyncState.SignedOut);
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    private SignInOutcome LogUnreachable(Exception e)
    {
        _log.Error("Sign-in failed.", e);
        // A rejection other than 401/429 (e.g. 400) still reads as "can't reach"; the server text is kept for the log only.
        return new SignInOutcome(SignInResult.Unreachable, null);
    }

    // ======================================================================== Session (HTTP, API, coordinator, engine)

    /// <summary>One signed-in session. Clipboard work runs through <see cref="TryRun"/> so it can be cancelled and drained.</summary>
    private sealed class Session(HttpClient http, PrivatePasteApi api, ClipboardCoordinator coordinator, SyncEngine engine)
    {
        private readonly object _gate = new();
        private readonly HashSet<Task> _inFlight = [];
        private readonly CancellationTokenSource _closing = new();
        private bool _closed;

        public HttpClient Http => http;
        public PrivatePasteApi Api => api;
        public ClipboardCoordinator Coordinator => coordinator;
        public SyncEngine Engine => engine;

        /// <summary>Starts tracked work (which must not throw); false once the session is closing.</summary>
        public bool TryRun(Func<CancellationToken, Task> work)
        {
            lock (_gate)
            {
                if (_closed) return false;
                var task = Task.Run(() => work(_closing.Token));
                _inFlight.Add(task);
                task.ContinueWith(t => { lock (_gate) _inFlight.Remove(t); }, TaskScheduler.Default);
                return true;
            }
        }

        /// <summary>Refuses new work, cancels the running work and waits for it to finish.</summary>
        public async Task CloseAsync()
        {
            Task[] pending;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                pending = [.. _inFlight];
            }
            await _closing.CancelAsync();
            try { await Task.WhenAll(pending); }
            catch (Exception) { }   // the work logs its own failures
            _closing.Dispose();
        }
    }

    // Caller holds _lifecycle.
    private void StartSession(Uri server, string token, Guid deviceId, HttpClient? http = null)
    {
        http ??= CreateHttpClient(server);
        var api = new PrivatePasteApi(http, _time) { Token = token };
        var coordinator = new ClipboardCoordinator(
            Platform.Clipboard, api, Cache, _queue, new EchoGuard(_time, EchoWindow), _notifier, _time, () => _paused,
            app => BlockedApps.Matches(_settings.BlockedApps, app),   // reads the current settings, so edits apply at once
            _log.Info);
        var wsUri = WebSocketUri(http.BaseAddress!);
        var engine = new SyncEngine(
            api, Cache, coordinator, ct => ConnectWithTimeoutAsync(t => _transport.Connect(wsUri, token, t), _time, ct), deviceId,
            _time, new Backoff(Random.Shared), () => _paused);

        var session = new Session(http, api, coordinator, engine);
        engine.StateChanged += (_, state) =>
        {
            if (state is SyncState.Connected or SyncState.Offline or SyncState.Paused) _log.Info($"Sync state: {state}.");
            if (_session == session) StateChanged?.Invoke(this, state);
        };
        engine.Error += (_, error) => _log.Error(error.Message, error.Exception);
        engine.ClipApplied += (_, clip) =>
        {
            if (_session == session && !_paused && _settings.NotifyOnClipArrival) ClipArrived?.Invoke(this, clip);
        };
        // Never block the engine loop: sign out on another task.
        engine.Revoked += (_, _) => _ = Task.Run(() => OnRevokedAsync(session));

        _session = session;
        engine.Start();
    }

    /// <summary>BaseAddress = normalized server URL with a trailing '/', so relative API paths keep any sub-path.</summary>
    private HttpClient CreateHttpClient(Uri server)
    {
        var text = server.AbsoluteUri;
        if (!text.EndsWith('/')) text += "/";
        // No client-wide timeout: PrivatePasteApi times each call itself (uploads scale with their size).
        return new HttpClient(_transport.CreateHandler()) { BaseAddress = new Uri(text), Timeout = Timeout.InfiniteTimeSpan };
    }

    public static readonly TimeSpan WebSocketConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runs a WebSocket connect with a <see cref="WebSocketConnectTimeout"/> limit, so a stalled TLS handshake or upgrade
    /// becomes a normal connect failure (<see cref="TimeoutException"/>: the engine backs off and retries). The caller's
    /// cancellation stays an OCE. A connect that ignores cancellation is abandoned and its late socket disposed.
    /// </summary>
    public static async Task<WebSocket> ConnectWithTimeoutAsync(Func<CancellationToken, Task<WebSocket>> connect, TimeProvider time,
        CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(WebSocketConnectTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var attempt = connect(linked.Token);
        try
        {
            return await attempt.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException e) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Abandon(attempt);
            throw new TimeoutException($"The server did not accept the connection within {WebSocketConnectTimeout.TotalSeconds:0} s.", e);
        }
        catch (OperationCanceledException)
        {
            Abandon(attempt);
            throw;
        }

        static void Abandon(Task<WebSocket> attempt) =>
            attempt.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
    }

    /// <summary>{base}ws with ws/wss matching http/https.</summary>
    public static Uri WebSocketUri(Uri baseAddress)
    {
        var builder = new UriBuilder(new Uri(baseAddress, "ws"))
        {
            Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        return builder.Uri;
    }


    // ======================================================================== History notifications

    private int _historyPending;

    /// <summary>
    /// Every cache change (sync, local uploads, queue flushes, clears) becomes <see cref="HistoryChanged"/>, coalesced
    /// and raised on the thread pool so a catch-up of hundreds of clips does not mean hundreds of UI refreshes.
    /// </summary>
    private void OnCacheChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _historyPending, 1) == 1) return;
        _ = Task.Run(() =>
        {
            Volatile.Write(ref _historyPending, 0);
            try { HistoryChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _log.Error("A history listener failed.", ex); }
        });
    }

    // ======================================================================== Clipboard

    // Each change goes straight to the coordinator: it reads first and orders captures itself (see its docs).
    // Task 8 owns the final wiring for the real clipboard watcher.
    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        var session = _session;
        session?.TryRun(async ct =>
        {
            try { await session.Coordinator.OnClipboardChangedAsync(ct); }
            // Not awaited: sign-out drains this very task while holding the lifecycle lock.
            catch (UnauthorizedApiException) { _ = Task.Run(() => OnRevokedAsync(session)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { _log.Error("Could not process a clipboard change.", ex); }
        });
    }

    // ======================================================================== Pause

    /// <summary>
    /// Pause just flips the persisted flag (the engine polls it). Resume first lets the coordinator remember what is on the
    /// clipboard (so nothing copied while paused is uploaded), and only then flips the flag. If that fails, it stays paused.
    /// </summary>
    public async Task SetPausedAsync(bool paused)
    {
        await _pauseGate.WaitAsync();
        try
        {
            if (paused == _paused) return;
            if (!paused && Coordinator is { } coordinator)
            {
                try { await coordinator.OnResumedAsync(_shutdown.Token); }
                catch (Exception e)
                {
                    _log.Error("Could not resume sync.", e);
                    _notifier.Notify("Could not resume sync. Try again.");
                    return;
                }
            }
            _paused = paused;
            UpdateSettings(s => s with { Paused = paused });
        }
        finally { _pauseGate.Release(); }
        PausedChanged?.Invoke(this, EventArgs.Empty);
    }

    // ======================================================================== Settings: hotkey, autostart

    public void SetHotkey(HotkeyGesture gesture)
    {
        UpdateSettings(s => s with { Hotkey = gesture.ToString() });
        HotkeyChanged?.Invoke(this, gesture);
    }

    public void SetNotifyOnClipArrival(bool enabled) => UpdateSettings(s => s with { NotifyOnClipArrival = enabled });

    public void SetAutostart(bool enabled)
    {
        UpdateSettings(s => s with { Autostart = enabled });
        ApplyAutostart();
    }

    /// <summary>Makes the OS login item match the setting (on by default; also refreshes the exe path).</summary>
    private void ApplyAutostart()
    {
        try { Platform.Autostart.Set(_settings.Autostart); }
        catch (Exception e)
        {
            _log.Error("Could not update autostart.", e);
            _notifier.Notify("Could not change the start-at-login setting.");
        }
    }

    private void UpdateSettings(Func<ClientSettings, ClientSettings> change)
    {
        lock (_settingsStore)
        {
            _settings = change(_settings);
            try { _settingsStore.Save(_settings); }
            catch (Exception e) { _log.Error("Could not save settings.", e); }
        }
    }

    // ======================================================================== Cache key and queue

    /// <summary>
    /// The 32-byte cache key from {DataDirectory}/cache.key. A new key is generated only when the file does not exist;
    /// null when it exists but cannot be read (transient lock or another user's data), in which case it is left alone.
    /// </summary>
    private byte[]? LoadOrCreateCacheKey()
    {
        var path = Path.Combine(DataDirectory, "cache.key");
        var file = new SecretFile(path, Platform.SecretProtector);
        if (file.Read() is { } stored)
        {
            try
            {
                var key = Convert.FromBase64String(stored);
                if (key.Length == 32) return key;
            }
            catch (FormatException) { }
            _log.Error("The cache key file is malformed; running without a saved history this session.");
            return null;
        }
        if (File.Exists(path))
        {
            _log.Error("The cache key file exists but could not be unlocked; running without a saved history this session.");
            return null;
        }
        try
        {
            var created = RandomNumberGenerator.GetBytes(32);
            file.Write(Convert.ToBase64String(created));
            return created;
        }
        catch (Exception e)
        {
            _log.Error("Could not create the cache key.", e);
            return null;
        }
    }

    /// <summary>
    /// The cache key could not be read at startup (this session runs on a throwaway key in a temp folder). A sign-in clears
    /// the history and the queue anyway, so nothing is lost by replacing the unreadable key with a fresh one: from now on
    /// history is kept in the data directory again. Caller holds _lifecycle, with no session running.
    /// </summary>
    private void LeaveEphemeralMode()
    {
        if (_ephemeralDirectory is not { } ephemeral) return;
        var key = RandomNumberGenerator.GetBytes(32);
        try { new SecretFile(Path.Combine(DataDirectory, "cache.key"), Platform.SecretProtector).Write(Convert.ToBase64String(key)); }
        catch (Exception e)
        {
            _log.Error("Could not replace the unreadable cache key; local history stays unavailable this session.", e);
            return;
        }
        Cache.Changed -= OnCacheChanged;
        Cache.Clear();
        (_cacheKey, _cachePath, _queuePath, _ephemeralDirectory) =
            (key, Path.Combine(DataDirectory, "cache.dat"), Path.Combine(DataDirectory, "queue.dat"), null);
        Cache = new HistoryCache(_cachePath, _cacheKey);
        Cache.Changed += OnCacheChanged;
        try { Directory.Delete(ephemeral, recursive: true); }
        catch (Exception) { }
        _log.Info("Replaced the unreadable cache key; local history is kept again.");
    }

    private void ClearQueue()
    {
        try
        {
            File.Delete(_queuePath);
            File.Delete(_queuePath + ".tmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Error("Could not delete the offline queue.", e);
        }
        _queue = OutboundQueue.Load(_queuePath, _cacheKey);
    }

    // ======================================================================== Network and power changes

    private static readonly TimeSpan NudgeCoalesce = TimeSpan.FromSeconds(2);
    private long _lastNudgeTicks;

    private void WatchNetworkAndPower()
    {
        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            if (OperatingSystem.IsWindows()) Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception e) { _log.Error("Could not watch network and power changes.", e); }
    }

    private void UnwatchNetworkAndPower()
    {
        try
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            if (OperatingSystem.IsWindows()) Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch (Exception) { }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) OnNetworkOrPowerChanged("Network available");
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => OnNetworkOrPowerChanged("Network address changed");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnNetworkOrPowerChanged("Resumed from sleep");
    }

    /// <summary>
    /// Resume from sleep or a network change: the socket is likely dead without knowing it, so the engine drops it, resets
    /// its backoff and reconnects at once (<see cref="SyncEngine.Nudge"/>). Bursts within 2 s count once.
    /// </summary>
    public void OnNetworkOrPowerChanged(string what)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastNudgeTicks);
        if (last != 0 && now - last < NudgeCoalesce.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastNudgeTicks, now, last) != last) return;   // another event got there first
        if (_session is not { } session) return;
        _log.Info($"{what}: reconnecting.");
        session.Engine.Nudge();
    }

    // ======================================================================== Shutdown

    /// <summary>
    /// Blocking dispose for synchronous exit paths such as the UI thread at OS logoff. Runs on the thread pool: awaited
    /// directly, continuations would be posted to the blocked UI thread's context and never run (deadlock until timeout,
    /// skipping the final cache save). True if it finished within <paramref name="timeout"/>.
    /// </summary>
    public bool DisposeWithin(TimeSpan timeout) => Task.Run(() => DisposeAsync().AsTask()).Wait(timeout);

    public async ValueTask DisposeAsync()
    {
        Platform.Clipboard.Changed -= OnClipboardChanged;
        UnwatchNetworkAndPower();
        await _shutdown.CancelAsync();
        await _lifecycle.WaitAsync();
        try
        {
            if (_session is { } session)
            {
                _session = null;
                await session.CloseAsync();
                await session.Engine.StopAsync();   // saves the cache
                session.Http.Dispose();
            }
        }
        finally { _lifecycle.Release(); }
        if (_ephemeralDirectory is not null)
        {
            try { Directory.Delete(_ephemeralDirectory, recursive: true); }
            catch (Exception) { }
        }
    }
}

/// <summary>How the host reaches the server: real sockets by default; tests substitute an in-process server.</summary>
public sealed class HostTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public required Func<HttpMessageHandler> CreateHandler { get; init; }

    /// <summary>(ws/wss URI, bearer token, ct) → an open WebSocket.</summary>
    public required Func<Uri, string, CancellationToken, Task<WebSocket>> Connect { get; init; }

    public static HostTransport Default { get; } = new()
    {
        CreateHandler = () => new SocketsHttpHandler { ConnectTimeout = ConnectTimeout },
        Connect = ConnectAsync,
    };

    private static async Task<WebSocket> ConnectAsync(Uri uri, string token, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        try
        {
            await socket.ConnectAsync(uri, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
