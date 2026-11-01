using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Desktop.Input;
using PrivatePaste.Protocol;
using PrivatePaste.Desktop.Logging;
using PrivatePaste.Desktop.Platform;
using PrivatePaste.Desktop.Tray;
using PrivatePaste.Desktop.ViewModels;
using PrivatePaste.Desktop.Views;

namespace PrivatePaste.Desktop;

public partial class App : Application
{
    /// <summary>Set by <see cref="Program"/> before the app starts.</summary>
    public static StartupOptions? Options { get; set; }
    public static FileLog? Log { get; set; }

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private DesktopHost? _host;
    private TrayController? _tray;
    private SignInWindow? _signIn;
    private SettingsWindow? _settings;
    private UnlockWindow? _unlock;
    private AddDeviceWindow? _addDevice;
    private PopupWindow? _popup;
    private HotkeyService? _hotkeys;
    private PasteInjector? _injector;
    private PopupActivation? _activation;
    private ToastNotifier? _toast;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        // Without startup options (designer, headless tests) only the theme is loaded.
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || Options is not { } options) return;

        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var log = Log ?? new FileLog(Path.Combine(options.DataDirectory, "logs", "privatepaste.log"));

        // ---- Composition ----
        var notifier = new ToastNotifier();
        var platform = PlatformServices.Create(Environment.ProcessPath ?? "PrivatePaste", options.Isolated, notifier, options.DataDirectory);
        _host = new DesktopHost(platform, options.DataDirectory, notifier, log);
        _tray = new TrayController(this, _host);

        // ---- Tray menu ----
        _tray.SignInRequested += (_, _) => ShowSignIn();
        _tray.UnlockRequested += (_, _) => ShowUnlock();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.SignOutRequested += (_, _) => _ = _host.SignOutAsync();
        _tray.OpenHistoryRequested += (_, _) =>
        {
            PopupTrace.Start("tray menu");
            if (_popup is null) _injector?.CapturePrevious();
            ShowPopup(CursorPoint());
        };
        _tray.QuitRequested += (_, _) => _ = QuitAsync();

        SetUpPopup(notifier);

        // ---- Host events ----
        _host.SignedOut += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _settings?.Close();
            _unlock?.Close();
            _addDevice?.Close();
            if (!_quitting) ShowSignIn();
        });
        _host.VaultStateChanged += (_, _) => Dispatcher.UIThread.Post(OnVaultStateChanged);

        // A clip from another device just landed on the clipboard (the host checks the setting). Raised on the sync loop:
        // build the content there and only post to the UI thread, so the loop never waits for the UI.
        _host.ClipArrived += (_, clip) => notifier.NotifyClip(ClipToast.For(clip));

        // OS logoff/shutdown or any other exit: stop the engine (it saves the cache) before the process ends.
        desktop.Exit += (_, _) => StopHost();

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            // --minimized and any start with a stored sign-in go straight to the tray; otherwise sign in first.
            if (!await _host!.StartAsync()) ShowSignIn();
        }
        catch (Exception e)
        {
            Log?.Error("Startup failed.", e);
            ShowSignIn();
        }
        _tray!.Refresh();
    }

    private void ShowSignIn()
    {
        if (_host is null || _host.IsSignedIn) return;
        if (_signIn is not null)
        {
            _signIn.Activate();
            return;
        }
        var vm = new SignInViewModel(_host.SignInAsync, _host.Settings.Username);
        if (_host.Settings.ServerUrl is { } last) vm.ServerUrl = last.TrimEnd('/');
        if (_host.Settings.DeviceName is { Length: > 0 } device) vm.DeviceName = device;
        var window = new SignInWindow { DataContext = vm };
        vm.SignedIn += (_, _) =>
        {
            window.Close();
            _tray?.Refresh();
        };
        window.Closed += (_, _) => _signIn = null;
        _signIn = window;
        window.Show();
    }

    private void ShowSettings()
    {
        if (_host is null) return;
        if (!_host.IsSignedIn)
        {
            ShowSignIn();
            return;
        }
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }
        var s = _host.Settings;
        var unlocked = _host.VaultState == VaultState.Unlocked;
        var vm = new SettingsViewModel(
            server: s.ServerUrl?.TrimEnd('/') ?? "", deviceName: s.DeviceName, hotkey: s.Hotkey, autostart: s.Autostart,
            autostartLabel: _host.Platform.AutostartLabel,
            applyHotkey: _host.SetHotkey, setAutostart: _host.SetAutostart, signOut: _host.SignOutAsync,
            notifyOnClipArrival: s.NotifyOnClipArrival, setNotifyOnClipArrival: _host.SetNotifyOnClipArrival,
            blockedApps: s.BlockedApps, recentSourceApps: () => _host?.Coordinator?.RecentSourceApps ?? [],
            setBlockedApps: _host.SetBlockedApps, canIdentifySourceApp: _host.Platform.CanIdentifySourceApp,
            isOwner: _host.IsOwner, changePassphrase: _host.ChangePassphraseAsync, addDevice: ShowAddDevice);
        vm.SetUnlocked(unlocked);
        var window = new SettingsWindow { DataContext = vm };
        window.Closed += (_, _) => _settings = null;
        _settings = window;
        window.Show();
    }

    // ======================================================================== Encryption: unlock, set up, add a device

    /// <summary>UI thread. Opens (or updates) the unlock window when the vault needs a passphrase, and closes it otherwise.</summary>
    private void OnVaultStateChanged()
    {
        if (_host is not { } host || _quitting) return;
        var state = host.VaultState;
        var unlocked = state == VaultState.Unlocked;
        // A code made under a key that is gone is useless: never leave it on screen.
        if (!unlocked) _addDevice?.Close();
        (_settings?.DataContext as SettingsViewModel)?.SetUnlocked(unlocked);
        if (_popup?.DataContext is PopupViewModel popup) popup.Banner = host.VaultMessage;
        if (state is VaultState.Locked or VaultState.NeedsOwnerSetup)
        {
            ShowUnlock();
            return;
        }
        _unlock?.Close();
        // Once per change: the tray keeps saying it.
        if (state != VaultState.Unknown && VaultCopy.For(state, keyWasReset: false) is { } message) _toast?.Notify(message);
    }

    private void ShowUnlock()
    {
        if (_host is not { IsSignedIn: true } host) return;
        var state = host.VaultState;
        if (state is not (VaultState.Locked or VaultState.NeedsOwnerSetup)) return;
        var mode = state == VaultState.NeedsOwnerSetup ? UnlockMode.Setup : UnlockMode.Unlock;
        var message = host.KeyWasReset ? VaultCopy.WasReset : null;
        if (_unlock is { DataContext: UnlockViewModel open })
        {
            open.Show(mode, message);
            _unlock.Activate();
            return;
        }
        var vm = new UnlockViewModel(host.UnlockAsync, host.SetUpVaultAsync, mode, message);
        var window = new UnlockWindow { DataContext = vm };
        vm.Done += (_, _) =>
        {
            window.Close();
            _tray?.Refresh();
        };
        window.Closed += (_, _) => { if (_unlock == window) _unlock = null; };
        _unlock = window;
        window.Show();
    }

    private void ShowAddDevice()
    {
        if (_host is not { } host) return;
        if (_addDevice is not null)
        {
            _addDevice.Activate();
            return;
        }
        var window = new AddDeviceWindow { DataContext = new AddDeviceViewModel(host.CreateHandoverAsync, TimeProvider.System) };
        window.Closed += (_, _) => { if (_addDevice == window) _addDevice = null; };
        _addDevice = window;
        window.Show();
    }

    // ======================================================================== History popup and global hotkey

    private void SetUpPopup(ToastNotifier notifier)
    {
        var host = _host!;
        _toast = notifier;
        _injector = new PasteInjector();
        _activation = new PopupActivation(
            isSignedIn: () => _host?.IsSignedIn == true,
            isPaused: () => _host?.Paused == true,
            restore: async (id, ct) => Codec().Decode(
                await (_host?.Api ?? throw new InvalidOperationException("Signed out.")).RestoreAsync(id, ct)),
            applyRemote: (clip, ct) => (_host?.Coordinator ?? throw new InvalidOperationException("Signed out.")).ApplyRemoteAsync(clip, ct),
            writeLocalText: (text, ct) => (_host ?? throw new InvalidOperationException("Shut down.")).Platform.Clipboard
                .WriteAsync(new ClipboardContent.Text(text), ct),
            injector: _injector, toast: notifier.Notify, time: TimeProvider.System, isMacOs: OperatingSystem.IsMacOS());

        _hotkeys = new HotkeyService();
        // Hook thread: capture the window you are in and the cursor now, then hand over to the UI thread.
        PopupTrace.Sink = message => Log?.Info(message);
        _hotkeys.Pressed += (_, _) =>
        {
            PopupTrace.Start(_popup is null ? "hotkey" : "hotkey (popup already open)");
            if (_popup is null) _injector.CapturePrevious();   // while the popup is up the hotkey only closes it
            var cursor = CursorPoint();
            Dispatcher.UIThread.Post(() => ShowPopup(cursor));
        };
        _hotkeys.Failed += (_, e) =>
        {
            Log?.Error("The global hotkey is unavailable.", e);
            notifier.Notify("The history hotkey is unavailable. Use the tray menu instead.");
        };
        // A saved hotkey replaces the match in the running hook, so the old combination stops working at once.
        host.HotkeyChanged += (_, gesture) => _hotkeys?.Register(gesture);
        _hotkeys.Register(HotkeyGesture.TryParse(host.Settings.Hotkey, out var g) && g is not null
            ? g
            : new HotkeyGesture(Ctrl: !OperatingSystem.IsMacOS(), Shift: true, Alt: false, Meta: OperatingSystem.IsMacOS(),
                SharpHook.Data.KeyCode.VcV));
    }

    private PixelPoint CursorPoint()
    {
        if (OperatingSystem.IsWindows() && NativeWindows.CursorPosition() is var (x, y)) return new PixelPoint(x, y);
        var (mx, my) = _hotkeys?.LastMousePosition ?? (0, 0);
        return new PixelPoint(mx, my);
    }

    /// <summary>UI thread. Toggles the popup; signed out, shows the sign-in window instead.</summary>
    private void ShowPopup(PixelPoint cursor)
    {
        if (_host is null || _quitting) return;
        if (_popup is not null)
        {
            PopupTrace.Write($"toggle: closing the open popup (visible {_popup.IsVisible}, active {_popup.IsActive})");
            _popup.Close();
            return;
        }
        if (!_host.IsSignedIn)
        {
            ShowSignIn();
            return;
        }
        var host = _host;
        var vm = new PopupViewModel(host.Cache, host.Codec is null ? null : SearchOlder, ActivateFromPopupAsync,
            TimeProvider.System, PinFromPopupAsync, ListPinnedFromServerAsync, message => _toast?.Notify(message))
        {
            Banner = host.VaultMessage,
        };
        var window = new PopupWindow { DataContext = vm };
        // History that arrives while the popup is open shows up in it (the query and selection are kept).
        EventHandler onHistory = (_, _) => Dispatcher.UIThread.Post(() => { if (_popup == window) vm.RefreshFromCache(); });
        host.HistoryChanged += onHistory;
        window.Closed += (_, _) =>
        {
            host.HistoryChanged -= onHistory;
            if (_popup == window) _popup = null;
        };
        if (window.KeepsFocusBelow && _hotkeys is { } hotkeys)
        {
            // The popup leaves the focus in the app below, so the hook hands it the keys (on the hook thread: decide fast,
            // act on the UI thread). Closing gives the keyboard back.
            KeyCapture capture = (key, rawCode, mask) =>
            {
                var routed = PopupKeyRouter.Route(key, mask, KeyText.Translate(rawCode, mask));
                if (routed.Action == PopupKeyAction.Pass) return false;
                if (routed.Action != PopupKeyAction.Swallow)
                    Dispatcher.UIThread.Post(() => { if (_popup == window) window.HandleKey(routed); });
                return true;
            };
            hotkeys.Capture = capture;
            window.Closed += (_, _) => { if (hotkeys.Capture == capture) hotkeys.Capture = null; };
        }
        _popup = window;
        window.ShowAt(cursor);
    }

    /// <summary>"Search older clips": the server can't search, so its history is paged through and matched on this device.</summary>
    private IAsyncEnumerable<ClipDto> SearchOlder(string query, CancellationToken ct) =>
        _host?.Api is { } api && _host.Codec is { } codec
            ? new OlderClipSearch(api, codec).SearchAsync(query, null, ct)
            : throw new VaultLockedException();

    private async Task<ClipDto> PinFromPopupAsync(Guid id, bool pinned) =>
        Codec().Decode(await (_host?.Api ?? throw new InvalidOperationException("Signed out.")).PinAsync(id, pinned));

    private async Task<IReadOnlyList<ClipDto>> ListPinnedFromServerAsync(string query, CancellationToken ct)
    {
        if (_host?.Api is not { } api || _host.Codec is not { } codec) return [];
        return (await api.ListPinnedAsync(null, 50, ct)).Items.Select(codec.Decode)
            .Where(c => query.Length == 0 || c.Text is { } t && t.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>The codec that opens what the server returns; there is none while the vault is locked.</summary>
    private ClipCodec Codec() => _host?.Codec ?? throw new InvalidOperationException("Locked.");

    private Task ActivateFromPopupAsync(ClipDto clip)
    {
        PopupTrace.Write($"picked a {clip.Kind} clip");
        _popup?.Close();   // hide first: the restore/paste must not happen with the popup holding the focus
        return _activation!.ActivateAsync(clip);
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        StopPopupAndHotkey();
        if (_host is not null) await _host.DisposeAsync();
        _host = null;
        _tray?.Dispose();
        _tray = null;
        _desktop?.Shutdown();
    }

    /// <summary>Stops the global hook (no more hotkey events) and closes the popup. Safe to call twice.</summary>
    private void StopPopupAndHotkey()
    {
        var hotkeys = _hotkeys;
        _hotkeys = null;
        hotkeys?.Dispose();
        try { _popup?.Close(); } catch (Exception) { }
    }

    private void StopHost()
    {
        StopPopupAndHotkey();
        var host = _host;
        _host = null;
        if (host is null) return;
        try
        {
            if (!host.DisposeWithin(TimeSpan.FromSeconds(5))) Log?.Error("Shutdown timed out.");
        }
        catch (Exception e) { Log?.Error("Shutdown failed.", e); }
        _tray?.Dispose();
    }
}
