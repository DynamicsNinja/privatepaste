using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Desktop.ViewModels;
using SkiaSharp;

namespace PrivatePaste.Desktop.Tray;

/// <summary>
/// The tray icon and its menu: status line, Unlock… or Set up encryption… (only while the vault needs it), Open history,
/// Pause/Resume, Settings…, Sign out (Sign in… while signed out), Quit.
/// The icon is drawn at 50% opacity while paused, offline or signed out. Must be created on the UI thread.
/// </summary>
public sealed class TrayController : IDisposable
{
    public const string SignedOutText = "Signed out";
    public const string PausedText = "Paused";
    public const string OfflineText = "Offline, retrying";
    public const string ConnectingText = "Connecting…";
    public const string LockedText = "Locked";
    public const string NeedsSetupText = "Encryption isn't set up";

    private readonly DesktopHost _host;
    private readonly TrayIcon _icon;
    private readonly WindowIcon _normalIcon;
    private readonly WindowIcon _dimmedIcon;
    private readonly NativeMenuItem _status = new() { IsEnabled = false };
    private readonly NativeMenuItem _vault = new();
    private readonly NativeMenuItem _openHistory = new();
    private readonly NativeMenuItem _pause = new();
    private readonly NativeMenuItem _settings = new("Settings…");
    private readonly NativeMenuItem _account = new();
    private readonly NativeMenuItem _quit = new("Quit");

    public TrayController(Application app, DesktopHost host)
    {
        _host = host;
        (_normalIcon, _dimmedIcon) = LoadIcons();

        _vault.Click += (_, _) => UnlockRequested?.Invoke(this, EventArgs.Empty);
        _openHistory.Click += (_, _) => OpenHistoryRequested?.Invoke(this, EventArgs.Empty);
        _pause.Click += (_, _) => _ = TogglePauseAsync();
        _settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _account.Click += (_, _) =>
        {
            if (_host.IsSignedIn) SignOutRequested?.Invoke(this, EventArgs.Empty);
            else SignInRequested?.Invoke(this, EventArgs.Empty);
        };
        _quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new NativeMenu();
        menu.Items.Add(_status);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_vault);
        menu.Items.Add(_openHistory);
        menu.Items.Add(_pause);
        menu.Items.Add(_settings);
        menu.Items.Add(_account);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_quit);

        _icon = new TrayIcon { Icon = _dimmedIcon, ToolTipText = "PrivatePaste", Menu = menu, IsVisible = true };
        _icon.Clicked += (_, _) =>
        {
            if (_host.IsSignedIn) OpenHistoryRequested?.Invoke(this, EventArgs.Empty);
            else SignInRequested?.Invoke(this, EventArgs.Empty);
        };
        TrayIcon.SetIcons(app, [_icon]);

        _host.StateChanged += OnHostChanged;
        _host.PausedChanged += OnHostChanged;
        _host.SignedOut += OnHostChanged;
        _host.HotkeyChanged += OnHostChanged;
        _host.VaultStateChanged += OnHostChanged;
        Refresh();
    }

    public event EventHandler? OpenHistoryRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? SignInRequested;
    public event EventHandler? SignOutRequested;
    public event EventHandler? QuitRequested;
    /// <summary>"Unlock…" or "Set up encryption…" was clicked.</summary>
    public event EventHandler? UnlockRequested;

    /// <summary>
    /// The status line text for a host state (the four strings of the spec, plus "Connecting…" while connecting). While
    /// the vault keeps sync from running, that comes first: its copy (<see cref="VaultCopy.For"/>), or Locked / not set up.
    /// </summary>
    public static string StatusText(bool signedIn, bool paused, SyncState state, Uri? server,
        VaultState vault = VaultState.Unlocked, string? vaultMessage = null) =>
        !signedIn || state == SyncState.SignedOut ? SignedOutText
        : vaultMessage is not null ? vaultMessage
        : vault == VaultState.Locked ? LockedText
        : vault == VaultState.NeedsOwnerSetup ? NeedsSetupText
        : paused ? PausedText
        : state == SyncState.Connected ? $"Connected to {server?.Authority}"
        : state == SyncState.Connecting ? ConnectingText
        : OfflineText;

    /// <summary>Updates the menu and icon from the host. UI thread only.</summary>
    public void Refresh()
    {
        var signedIn = _host.IsSignedIn;
        var state = _host.State;
        var vault = _host.VaultState;
        var text = StatusText(signedIn, _host.Paused, state, _host.ServerUri, vault, _host.VaultMessage);
        _status.Header = text;
        _vault.Header = vault == VaultState.NeedsOwnerSetup ? "Set up encryption…" : "Unlock…";
        _vault.IsVisible = signedIn && vault is VaultState.Locked or VaultState.NeedsOwnerSetup;
        _icon.ToolTipText = "PrivatePaste: " + text;
        _icon.Icon = signedIn && !_host.Paused && state == SyncState.Connected ? _normalIcon : _dimmedIcon;

        _openHistory.Header = $"Open history ({_host.Settings.Hotkey})";
        _openHistory.IsEnabled = signedIn;
        _pause.Header = _host.Paused ? "Resume sync" : "Pause sync";
        _pause.IsEnabled = signedIn;
        _settings.IsEnabled = signedIn;
        _account.Header = signedIn ? "Sign out" : "Sign in…";
    }

    private void OnHostChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void OnHostChanged(object? sender, SyncState e) => Dispatcher.UIThread.Post(Refresh);

    private void OnHostChanged(object? sender, Input.HotkeyGesture e) => Dispatcher.UIThread.Post(Refresh);

    private void OnHostChanged(object? sender, VaultState e) => Dispatcher.UIThread.Post(Refresh);

    private async Task TogglePauseAsync()
    {
        await _host.SetPausedAsync(!_host.Paused);
        Refresh();
    }

    /// <summary>The app icon at tray size, plus a copy at 50% opacity.</summary>
    private static (WindowIcon Normal, WindowIcon Dimmed) LoadIcons()
    {
        const int size = 64;
        using var stream = AssetLoader.Open(new Uri("avares://PrivatePaste/Assets/app-icon-512.png"));
        using var original = SKBitmap.Decode(stream);
        using var scaled = original.Resize(new SKImageInfo(size, size), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var image = SKImage.FromBitmap(scaled);
        return (ToIcon(image, 1f), ToIcon(image, 0.5f));

        static WindowIcon ToIcon(SKImage image, float opacity)
        {
            using var surface = SKSurface.Create(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * opacity)) };
            surface.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
            using var snapshot = surface.Snapshot();
            using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return new WindowIcon(new MemoryStream(png.ToArray()));
        }
    }

    public void Dispose()
    {
        _host.StateChanged -= OnHostChanged;
        _host.PausedChanged -= OnHostChanged;
        _host.SignedOut -= OnHostChanged;
        _host.HotkeyChanged -= OnHostChanged;
        _host.VaultStateChanged -= OnHostChanged;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
