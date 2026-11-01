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
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Desktop.Input;
using PrivatePaste.Desktop.Logging;
using PrivatePaste.Desktop.Platform;
using PrivatePaste.Desktop.ViewModels;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;

namespace PrivatePaste.Desktop;

/// <summary>
/// Composition root: owns settings, secrets, the local cache and offline queue, the vault key, and, while signed in, one
/// <see cref="Session"/> (HTTP client, API, coordinator, sync engine). UI-free; events are raised on background
/// threads, so UI subscribers must marshal to the UI thread.
/// <para>
/// The vault state machine runs at start and after each sign-in (<see cref="VaultState"/>): a server without
/// <c>/api/server-info</c> is <see cref="ViewModels.VaultState.ServerTooOld"/>. Local history or queued copies that weren't
/// opened with the server's current key (from before encryption, or from before a reset this computer missed) are wiped,
/// with the stored key, before anything syncs. Then no vault is <see cref="ViewModels.VaultState.NeedsOwnerSetup"/> for the
/// owner and <see cref="ViewModels.VaultState.WaitingForOwner"/> for others; a stored key with the vault's key id is
/// <see cref="ViewModels.VaultState.Unlocked"/> (and only then does the engine start), anything else
/// <see cref="ViewModels.VaultState.Locked"/>. A key reset wipes the same way and locks; signing out or being revoked
/// deletes the key too. The copy returned by <see cref="UnlockAsync"/>, <see cref="SetUpVaultAsync"/> and
/// <see cref="ChangePassphraseAsync"/> is the same as Android's. Never log passphrases, keys or the handover QR text.
/// </para>
/// </summary>
public sealed class DesktopHost : IAsyncDisposable
{
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(5);
    /// <summary>The first wait before asking the server about its vault again after it couldn't be reached; doubles up to <see cref="CheckRetryMax"/>.</summary>
    private static readonly TimeSpan CheckRetryFirst = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CheckRetryMax = TimeSpan.FromMinutes(1);
    /// <summary>How often a state only someone else can change (no vault yet, an old server) is asked about again.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

    private readonly INotifier _notifier;
    private readonly FileLog _log;
    private readonly TimeProvider _time;
    private readonly HostTransport _transport;
    private readonly SettingsStore _settingsStore;
    private readonly SecretFile _tokenFile;
    private readonly VaultKeyStore _vaultKeyStore;
    private readonly object _keyGate = new();
    // The host owns the vault key; codecs (and so the session) only borrow it. Replaced as a pair under _keyGate.
    private VaultKey? _vaultKey;
    private volatile ClipCodec? _codec;
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
    private volatile VaultState _vaultState = VaultState.Unknown;
    private volatile bool _isOwner;
    private volatile bool _keyWasReset;
    /// <summary>Bumped by every sign-in, sign-out and start: a vault check loop of an earlier one stops.</summary>
    private int _checkEpoch;
    private volatile CancellationTokenSource? _checkWait;

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
        _vaultKeyStore = new VaultKeyStore(Path.Combine(dataDirectory, "vault.dat"), platform.SecretProtector);
        if (_vaultKeyStore.Load() is { } storedKey) ReplaceVaultKey(storedKey);
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

    /// <summary>
    /// Seals and opens clips with this device's vault key; null unless <see cref="VaultState"/> is Unlocked (a stored key
    /// is used only once the server confirmed it is still the vault's).
    /// </summary>
    public ClipCodec? Codec => _vaultState == VaultState.Unlocked ? _codec : null;

    /// <summary>Where this computer stands with the server's encryption. Unknown while signed out or not asked yet.</summary>
    public VaultState VaultState => _vaultState;

    /// <summary>Whether this account is the server's owner, as of the last vault check.</summary>
    public bool IsOwner => _isOwner;

    /// <summary>The owner reset encryption while this computer held the old key: the unlock window says so.</summary>
    public bool KeyWasReset => _keyWasReset;

    /// <summary>The copy for the current <see cref="VaultState"/> (tray, popup banner), or null (see <see cref="VaultCopy.For"/>).</summary>
    public string? VaultMessage => VaultCopy.For(_vaultState, _keyWasReset);

    /// <summary>Raised on a background thread whenever <see cref="VaultState"/> changes.</summary>
    public event EventHandler<VaultState>? VaultStateChanged;

    /// <summary>
    /// Stores <paramref name="key"/> as this device's vault key (protected like the sign-in token) and uses it from now on.
    /// The host takes ownership: the previous key is disposed, and anything still using it fails cleanly.
    /// </summary>
    public void SetVaultKey(VaultKey key)
    {
        _vaultKeyStore.Save(key);
        ReplaceVaultKey(key);
    }

    private void ReplaceVaultKey(VaultKey? key)
    {
        VaultKey? old;
        lock (_keyGate)
        {
            old = _vaultKey;
            _vaultKey = key;
            _codec = key is null ? null : new ClipCodec(key);
        }
        if (!ReferenceEquals(old, key)) old?.Dispose();
    }

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
            BeginVaultCheck();
            _log.Info("Resumed the stored session.");
            return true;
        }
        finally { _lifecycle.Release(); }
    }

    // ======================================================================== Sign-in / sign-out

    public async Task<SignInOutcome> SignInAsync(string serverUrl, string username, string password, string deviceName)
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
                login = await new PrivatePasteApi(http, _time).LoginAsync(username.Trim(), password, deviceName,
                    Platform.PlatformName, _shutdown.Token);
            }
            catch (Exception e)
            {
                http.Dispose();
                return e switch
                {
                    UnauthorizedApiException => new SignInOutcome(SignInResult.WrongPassword, null),
                    ApiException { Status: HttpStatusCode.TooManyRequests } => new SignInOutcome(SignInResult.RateLimited, null),
                    // Only a newer server rejects the request this way; its detail says what to do.
                    ApiException { Status: HttpStatusCode.BadRequest } bad => new SignInOutcome(SignInResult.UpdateNeeded, bad.Message),
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
            UpdateSettings(s => s with { ServerUrl = server.AbsoluteUri, DeviceName = deviceName, DeviceId = login.DeviceId,
                Username = username.Trim() });
            StartSession(server, login.Token, login.DeviceId, http);
            BeginVaultCheck();
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
        DeleteVaultKey();   // it belongs to this account's server
        UpdateSettings(s => s with { DeviceId = null });
        Interlocked.Increment(ref _checkEpoch);
        Cancel(_checkWait);
        _isOwner = false;
        _keyWasReset = false;
        SetVaultState(VaultState.Unknown);
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

    /// <summary>
    /// One signed-in session. Clipboard work runs through <see cref="TryRun"/> so it can be cancelled and drained. A wipe of
    /// the local data (a key reset) replaces it with a new one on the same sign-in.
    /// </summary>
    private sealed class Session(HttpClient http, PrivatePasteApi api, ClipboardCoordinator coordinator, SyncEngine engine,
        Uri server, string token, Guid deviceId)
    {
        private readonly object _gate = new();
        private readonly HashSet<Task> _inFlight = [];
        private readonly CancellationTokenSource _closing = new();
        private bool _closed;

        public HttpClient Http => http;
        public PrivatePasteApi Api => api;
        public ClipboardCoordinator Coordinator => coordinator;
        public SyncEngine Engine => engine;
        public Uri Server => server;
        public string Token => token;
        public Guid DeviceId => deviceId;

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

    /// <summary>
    /// Builds a session and makes it the current one. Caller holds _lifecycle. The engine is not started: that waits for
    /// the vault check (<see cref="ApplyCheckAsync"/>) or an unlock to say this computer holds the vault's key.
    /// </summary>
    private Session StartSession(Uri server, string token, Guid deviceId, HttpClient? http = null)
    {
        http ??= CreateHttpClient(server);
        var api = new PrivatePasteApi(http, _time) { Token = token };
        // Both take the codec only while unlocked: until then copies wait in the queue and nothing is fetched.
        var coordinator = new ClipboardCoordinator(
            Platform.Clipboard, api, () => Codec, Cache, _queue, new EchoGuard(_time, EchoWindow), _notifier, _time, () => _paused,
            app => BlockedApps.Matches(_settings.BlockedApps, app),   // reads the current settings, so edits apply at once
            _log.Info);
        var wsUri = WebSocketUri(http.BaseAddress!);
        var engine = new SyncEngine(
            api, Cache, coordinator, () => Codec, ct => ConnectWithTimeoutAsync(t => _transport.Connect(wsUri, token, t), _time, ct),
            deviceId, _time, new Backoff(Random.Shared), () => _paused);

        var session = new Session(http, api, coordinator, engine, server, token, deviceId);
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
        // Never block the engine loop (or the coordinator's gate, which a reset can be raised under): act on another task.
        engine.Revoked += (_, _) => _ = Task.Run(() => OnRevokedAsync(session));
        engine.KeyReset += (_, _) => _ = Task.Run(() => OnKeyResetAsync(session));
        engine.UpdateRequired += (_, _) => _ = Task.Run(() => SettleAsync(session, VaultState.UpdateRequired));

        _session = session;
        return session;
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


    // ======================================================================== Vault: the state machine

    /// <summary>Starts asking the server about its vault for the current sign-in: in the background, retried while offline.</summary>
    private void BeginVaultCheck()
    {
        var epoch = Interlocked.Increment(ref _checkEpoch);
        _ = Task.Run(() => VaultCheckLoopAsync(epoch));
    }

    private async Task VaultCheckLoopAsync(int epoch)
    {
        var retry = CheckRetryFirst;
        while (!_shutdown.IsCancellationRequested && Volatile.Read(ref _checkEpoch) == epoch && _session is { } session)
        {
            TimeSpan wait;
            if (await CheckVaultAsync(session).ConfigureAwait(false))
            {
                // Settled. Only what someone else changes is asked about again: the owner sets up, the server is updated.
                if (_vaultState is not (VaultState.WaitingForOwner or VaultState.ServerTooOld)) return;
                wait = RecheckInterval;
                retry = CheckRetryFirst;
            }
            else
            {
                wait = retry;
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, CheckRetryMax.Ticks));
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _checkWait = cts;
            try { await Task.Delay(wait, _time, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }   // a network change, a sign-out or shutdown: the loop condition decides
            finally { _checkWait = null; }
        }
    }

    /// <summary>Asks the server once and settles the state. False when it couldn't be asked (offline, a server error).</summary>
    private async Task<bool> CheckVaultAsync(Session session)
    {
        VaultWrapDto? vault;
        bool owner;
        try
        {
            if (await session.Api.GetServerInfoAsync(_shutdown.Token).ConfigureAwait(false) is null)
            {
                await SettleAsync(session, VaultState.ServerTooOld).ConfigureAwait(false);
                return true;
            }
            vault = await session.Api.GetVaultAsync(_shutdown.Token).ConfigureAwait(false);
            owner = (await session.Api.GetMeAsync(_shutdown.Token).ConfigureAwait(false)).IsOwner;
        }
        catch (UpdateRequiredException)
        {
            await SettleAsync(session, VaultState.UpdateRequired).ConfigureAwait(false);
            return true;
        }
        catch (UnauthorizedApiException)
        {
            _ = Task.Run(() => OnRevokedAsync(session));
            return true;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e)
        {
            _log.Error("Could not ask the server about encryption; trying again.", e);
            return false;
        }
        await ApplyCheckAsync(session, vault, owner).ConfigureAwait(false);
        return true;
    }

    /// <summary>Steps 2 to 4 of the state machine (see the class docs) with the server's answer.</summary>
    private async Task ApplyCheckAsync(Session session, VaultWrapDto? vault, bool owner)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session != session) return;   // signed out or replaced meanwhile
            _isOwner = owner;
            // Already syncing: key changes reach the engine (a reset message, a foreign key id, a 409).
            if (_vaultState == VaultState.Unlocked) return;
            var serverKeyId = vault?.KeyId;
            if (HasLocalData() && (Cache.KeyId is null || Cache.KeyId != serverKeyId))
            {
                _log.Info("Local history belongs to another key (or predates encryption); clearing it.");
                session = await WipeLocalDataAsync(session).ConfigureAwait(false);
            }
            if (HeldKeyId() is { } held && held != serverKeyId) DeleteVaultKey();
            if (serverKeyId is not null) ClaimLocalData(serverKeyId);
            var state = vault is null ? (owner ? VaultState.NeedsOwnerSetup : VaultState.WaitingForOwner)
                : HeldKeyId() is not null ? VaultState.Unlocked
                : VaultState.Locked;
            SetVaultState(state);
            if (state == VaultState.Unlocked) session.Engine.Start();
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// The owner reset encryption (a reset message, a clip under another key id, or a 409 on upload): the stored key opens
    /// nothing new, and the history it opened is gone from the server. Wipes both and locks; the sign-in stays. Runs off the
    /// engine loop and the coordinator's gate (the event can be raised under either).
    /// </summary>
    private async Task OnKeyResetAsync(Session session)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session != session) return;
            _log.Info("Encryption was reset on the server; local history cleared until the new passphrase is entered.");
            await WipeLocalDataAsync(session).ConfigureAwait(false);
            _keyWasReset = true;
            SetVaultState(VaultState.Locked);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Clears the history (thumbnails included), the queue and the stored key, and replaces the session with a fresh one on
    /// the same sign-in (its engine not started), so nothing of the old one writes them again. Caller holds _lifecycle.
    /// </summary>
    private async Task<Session> WipeLocalDataAsync(Session session)
    {
        await session.CloseAsync().ConfigureAwait(false);
        await session.Engine.StopAsync().ConfigureAwait(false);
        Cache.Clear();
        ClearQueue();
        DeleteVaultKey();
        return StartSession(session.Server, session.Token, session.DeviceId, session.Http);
    }

    private async Task SettleAsync(Session session, VaultState state)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session == session) SetVaultState(state);
        }
        finally { _lifecycle.Release(); }
    }

    private void SetVaultState(VaultState state)
    {
        if (_vaultState == state) return;
        _vaultState = state;
        _log.Info($"Encryption: {state}.");
        try { VaultStateChanged?.Invoke(this, state); }
        catch (Exception e) { _log.Error("A vault state listener failed.", e); }
    }

    private bool HasLocalData() =>
        Cache.Items.Count > 0 || Cache.HighestSeq > 0 || Cache.LastAppliedAt is not null || _queue.Count > 0;

    /// <summary>Marks what is held now (queued copies too) as belonging to <paramref name="keyId"/>, so a restart keeps it.</summary>
    private void ClaimLocalData(string keyId)
    {
        if (Cache.KeyId == keyId) return;
        Cache.KeyId = keyId;
        try { Cache.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Error("Could not save local history.", e); }
    }

    private string? HeldKeyId()
    {
        lock (_keyGate) return _vaultKey?.KeyId;
    }

    private void DeleteVaultKey()
    {
        try { _vaultKeyStore.Delete(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Error("Could not delete the vault key.", e); }
        ReplaceVaultKey(null);
    }

    // ======================================================================== Vault: unlock, set up, change, hand over

    /// <summary>Unlocks with the passphrase (Argon2 runs off the calling thread). Null when unlocked; otherwise the copy to show.</summary>
    public async Task<string?> UnlockAsync(string passphrase)
    {
        if (_session is not { } session) return null;
        try
        {
            var wrap = await AskAsync(session, VaultCopy.UnlockFailed, ct => session.Api.GetVaultAsync(ct)).ConfigureAwait(false);
            if (wrap is null)
            {
                await CheckVaultAsync(session).ConfigureAwait(false);   // the vault is gone: set up, or wait for the owner
                return null;
            }
            return await KeepAsync(session, await UnwrapAsync(wrap, passphrase).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Shown e) { return e.Copy; }
    }

    /// <summary>The owner sets up encryption with a new key. Null when done (unlocked); otherwise the copy to show.</summary>
    public async Task<string?> SetUpVaultAsync(string passphrase, string repeat)
    {
        if (VaultCopy.NewPassphraseProblem(passphrase, repeat) is { } problem) return problem;
        if (_session is not { } session) return null;
        VaultKey? owned = VaultKey.Create();
        try
        {
            var key = owned;
            var wrap = await Task.Run(() => PassphraseWrap.Wrap(key, passphrase)).ConfigureAwait(false);
            try
            {
                await AskAsync(session, VaultCopy.SetupFailed, async ct =>
                {
                    await session.Api.PutVaultAsync(wrap, ct).ConfigureAwait(false);
                    return true;
                }, forbidden: VaultCopy.WaitingForOwner, conflict: VaultCopy.SetUpElsewhere).ConfigureAwait(false);
            }
            catch (Shown)
            {
                await CheckVaultAsync(session).ConfigureAwait(false);   // set up meanwhile (409), or not the owner after all (403)
                throw;
            }
            owned = null;
            return await KeepAsync(session, key).ConfigureAwait(false);
        }
        catch (Shown e) { return e.Copy; }
        finally { owned?.Dispose(); }
    }

    /// <summary>The owner re-wraps the same key under <paramref name="next"/>, proving <paramref name="current"/> first. Null when changed.</summary>
    public async Task<string?> ChangePassphraseAsync(string current, string next, string repeat)
    {
        if (VaultCopy.NewPassphraseProblem(next, repeat) is { } problem) return problem;
        if (_session is not { } session) return VaultCopy.ChangeFailed;
        try
        {
            var wrap = await AskAsync(session, VaultCopy.ChangeFailed, ct => session.Api.GetVaultAsync(ct)).ConfigureAwait(false)
                ?? throw new Shown(VaultCopy.ChangeFailed);
            VaultKey key;
            try { key = await UnwrapAsync(wrap, current).ConfigureAwait(false); }
            catch (Shown e) when (e.Copy == VaultCopy.WrongPassphrase) { throw new Shown(VaultCopy.WrongCurrentPassphrase); }
            using var _ = key;
            var rewrapped = await Task.Run(() => PassphraseWrap.Wrap(key, next)).ConfigureAwait(false);
            await AskAsync(session, VaultCopy.ChangeFailed, async ct =>
            {
                await session.Api.PutVaultAsync(rewrapped, ct).ConfigureAwait(false);
                return true;
            }, forbidden: VaultCopy.OwnerOnly).ConfigureAwait(false);
            _log.Info("Changed the passphrase.");
            return null;
        }
        catch (Shown e) { return e.Copy; }
    }

    /// <summary>
    /// Hands this computer's vault key to a new device: the server keeps the sealed key for 10 minutes, and the returned
    /// QR text holds the one-time key that opens it. Never log the QR text.
    /// </summary>
    /// <exception cref="InvalidOperationException">Signed out or not unlocked.</exception>
    /// <exception cref="HttpRequestException">Offline.</exception>
    /// <exception cref="ApiException">The server refused.</exception>
    public async Task<(HandoverCreated Created, string QrText)> CreateHandoverAsync(CancellationToken ct = default)
    {
        var session = _session ?? throw new InvalidOperationException("Signed out.");
        byte[] otk;
        byte[] blob;
        string keyId;
        lock (_keyGate)
        {
            if (_vaultState != VaultState.Unlocked || _vaultKey is not { } key) throw new InvalidOperationException("Locked.");
            (otk, blob) = Handover.Wrap(key);
            keyId = key.KeyId;
        }
        try
        {
            var created = await session.Api.CreateHandoverAsync(blob, ct).ConfigureAwait(false);
            return (created, Handover.QrText(created.Id, otk, keyId));
        }
        finally { CryptographicOperations.ZeroMemory(otk); }
    }

    /// <summary>Argon2 off the calling thread; the two failures become their copy.</summary>
    private static async Task<VaultKey> UnwrapAsync(VaultWrapDto wrap, string passphrase)
    {
        try { return await Task.Run(() => PassphraseWrap.Unwrap(wrap, passphrase)).ConfigureAwait(false); }
        catch (WrongPassphraseException) { throw new Shown(VaultCopy.WrongPassphrase); }
        catch (UnsupportedWrapException) { throw new Shown(VaultCopy.Unsupported); }
    }

    /// <summary>
    /// Keeps <paramref name="key"/> as this sign-in's unlocked key and starts syncing. Owns the key: it is disposed when not
    /// kept. Local data from another key is wiped first. Null when kept (or signed out meanwhile); otherwise the copy.
    /// </summary>
    private async Task<string?> KeepAsync(Session session, VaultKey key)
    {
        var kept = false;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            // The same sign-in, even if a wipe replaced the session object meanwhile.
            if (_session is not { } current || current.Token != session.Token) return null;
            if (HasLocalData() && Cache.KeyId != key.KeyId) current = await WipeLocalDataAsync(current).ConfigureAwait(false);
            try { _vaultKeyStore.Save(key); }
            catch (Exception e)
            {
                _log.Error("Could not store the vault key.", e);
                return VaultCopy.SaveFailed;
            }
            ReplaceVaultKey(key);
            kept = true;
            ClaimLocalData(key.KeyId);
            _keyWasReset = false;
            SetVaultState(VaultState.Unlocked);
            current.Engine.Start();
            return null;
        }
        finally
        {
            _lifecycle.Release();
            if (!kept) key.Dispose();
        }
    }

    /// <summary>Copy to show, thrown out of a call by <see cref="AskAsync{T}"/> and caught by the public method.</summary>
    private sealed class Shown(string copy) : Exception(copy)
    {
        public string Copy => Message;
    }

    /// <summary>
    /// Runs a server call, turning its failures into copy (<see cref="Shown"/>): <paramref name="forbidden"/> for a 403 and
    /// <paramref name="conflict"/> for a 409 when given, else the action's <paramref name="failed"/> line. The server's own
    /// text is never shown. A 401 signs out (the device was revoked).
    /// </summary>
    private async Task<T> AskAsync<T>(Session session, string failed, Func<CancellationToken, Task<T>> call,
        string? forbidden = null, string? conflict = null)
    {
        try
        {
            return await call(_shutdown.Token).ConfigureAwait(false);
        }
        catch (UpdateRequiredException)
        {
            await SettleAsync(session, VaultState.UpdateRequired).ConfigureAwait(false);
            throw new Shown(VaultCopy.UpdateRequired);
        }
        catch (UnauthorizedApiException)
        {
            _ = Task.Run(() => OnRevokedAsync(session));
            throw new Shown(failed);
        }
        catch (ApiException e)
        {
            throw new Shown(e.Status switch
            {
                HttpStatusCode.Forbidden when forbidden is not null => forbidden,
                HttpStatusCode.Conflict when conflict is not null => conflict,
                _ => failed,
            });
        }
        catch (HttpRequestException)
        {
            throw new Shown(SignInViewModel.UnreachableMessage);
        }
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
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

    public void SetBlockedApps(IReadOnlyList<string> apps) => UpdateSettings(s => s with { BlockedApps = apps });

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
        Cancel(_checkWait);   // a vault check waiting to retry asks now
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
        ReplaceVaultKey(null);   // after the session stopped: nothing uses it any more
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
        socket.Options.SetRequestHeader(ProtocolHeaders.Name, ProtocolHeaders.Version);
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
