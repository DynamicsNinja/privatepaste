using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.SignOutRequested += (_, _) => _ = _host.SignOutAsync();
        _tray.OpenHistoryRequested += (_, _) => { };   // Task 9: history popup
        _tray.QuitRequested += (_, _) => _ = QuitAsync();

        // ---- Host events ----
        _host.SignedOut += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _settings?.Close();
            if (!_quitting) ShowSignIn();
        });

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
        var vm = new SignInViewModel(_host.SignInAsync);
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
        var vm = new SettingsViewModel(
            server: s.ServerUrl?.TrimEnd('/') ?? "", deviceName: s.DeviceName, hotkey: s.Hotkey, autostart: s.Autostart,
            autostartLabel: _host.Platform.AutostartLabel,
            applyHotkey: _host.SetHotkey, setAutostart: _host.SetAutostart, signOut: _host.SignOutAsync);
        var window = new SettingsWindow { DataContext = vm };
        window.Closed += (_, _) => _settings = null;
        _settings = window;
        window.Show();
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        if (_host is not null) await _host.DisposeAsync();
        _host = null;
        _tray?.Dispose();
        _tray = null;
        _desktop?.Shutdown();
    }

    private void StopHost()
    {
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
