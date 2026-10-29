using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivatePaste.Desktop.Input;
using AppNames = PrivatePaste.Client.Core.Settings.BlockedApps;

namespace PrivatePaste.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    public const string InvalidHotkeyMessage = "Use a combination like Ctrl+Shift+V.";
    public const string CannotBlockAppsMessage = "This system can't tell which app copied something, so apps can't be blocked here. "
        + "Copies that password managers mark as private are still ignored.";

    private readonly Action<HotkeyGesture> _applyHotkey;
    private readonly Action<bool> _setAutostart;
    private readonly Func<Task> _signOut;
    private readonly Action<bool> _setNotifyOnClipArrival;
    private readonly Func<IReadOnlyList<string>> _recentSourceApps;
    private readonly Action<IReadOnlyList<string>> _setBlockedApps;
    private string _appliedHotkey;

    public SettingsViewModel(
        string server, string deviceName, string hotkey, bool autostart, string autostartLabel,
        Action<HotkeyGesture> applyHotkey, Action<bool> setAutostart, Func<Task> signOut,
        bool notifyOnClipArrival = true, Action<bool>? setNotifyOnClipArrival = null,
        IReadOnlyList<string>? blockedApps = null, Func<IReadOnlyList<string>>? recentSourceApps = null,
        Action<IReadOnlyList<string>>? setBlockedApps = null, bool canIdentifySourceApp = true)
    {
        Server = server;
        DeviceName = deviceName;
        AutostartLabel = autostartLabel;
        _hotkey = hotkey;
        _appliedHotkey = hotkey;
        _startWithOs = autostart;
        _applyHotkey = applyHotkey;
        _setAutostart = setAutostart;
        _signOut = signOut;
        _notifyOnClipArrival = notifyOnClipArrival;
        _setNotifyOnClipArrival = setNotifyOnClipArrival ?? (_ => { });
        _recentSourceApps = recentSourceApps ?? (() => []);
        _setBlockedApps = setBlockedApps ?? (_ => { });
        CanBlockApps = canIdentifySourceApp;
        BlockedApps = new ObservableCollection<string>(blockedApps ?? []);
    }

    public string Server { get; }
    public string DeviceName { get; }

    /// <summary>"Start with Windows" or "Start at login".</summary>
    public string AutostartLabel { get; }

    [ObservableProperty]
    private string _hotkey;

    [ObservableProperty]
    private string? _hotkeyError;

    [ObservableProperty]
    private bool _startWithOs;

    partial void OnHotkeyChanged(string value) => HotkeyError = null;

    /// <summary>Show a toast when a clip from another device lands on the clipboard. Persisted; on by default.</summary>
    [ObservableProperty]
    private bool _notifyOnClipArrival;

    partial void OnStartWithOsChanged(bool value) => _setAutostart(value);

    partial void OnNotifyOnClipArrivalChanged(bool value) => _setNotifyOnClipArrival(value);

    /// <summary>Validates the typed hotkey; a valid, changed one is applied in canonical form.</summary>
    [RelayCommand]
    private void ApplyHotkey()
    {
        if (!HotkeyGesture.TryParse(Hotkey, out var gesture))
        {
            HotkeyError = InvalidHotkeyMessage;
            return;
        }
        var canonical = gesture!.ToString();
        Hotkey = canonical;
        HotkeyError = null;
        if (canonical == _appliedHotkey) return;
        _appliedHotkey = canonical;
        _applyHotkey(gesture);
    }

    [RelayCommand]
    private Task SignOutAsync() => _signOut();

    // ---- Private copies ----

    /// <summary>False where the OS cannot tell which app copied something: the section then only explains that.</summary>
    public bool CanBlockApps { get; }

    public bool CannotBlockApps => !CanBlockApps;

    /// <summary>Apps whose copies are ignored, in the form they are stored (lowercase, no ".exe").</summary>
    public ObservableCollection<string> BlockedApps { get; }

    public bool HasBlockedApps => BlockedApps.Count > 0;

    public bool HasNoBlockedApps => BlockedApps.Count == 0;

    /// <summary>The apps that copied recently and are not blocked yet (the "Add" menu).</summary>
    public IReadOnlyList<string> AddableApps =>
        _recentSourceApps().Where(a => AppNames.Normalize(a) is { Length: > 0 } n && !BlockedApps.Contains(n)).ToArray();

    /// <summary>True while the "Type a name…" box is open.</summary>
    [ObservableProperty]
    private bool _isTypingApp;

    [ObservableProperty]
    private string _typedApp = "";

    [RelayCommand]
    private void AddBlockedApp(string? name)
    {
        var app = AppNames.Normalize(name ?? "");
        if (app.Length == 0 || BlockedApps.Contains(app)) return;
        BlockedApps.Add(app);
        SaveBlockedApps();
    }

    [RelayCommand]
    private void RemoveBlockedApp(string? name)
    {
        if (name is null || !BlockedApps.Remove(name)) return;
        SaveBlockedApps();
    }

    [RelayCommand]
    private void StartTypingApp()
    {
        TypedApp = "";
        IsTypingApp = true;
    }

    [RelayCommand]
    private void ConfirmTypedApp()
    {
        AddBlockedApp(TypedApp);
        CancelTypingApp();
    }

    [RelayCommand]
    private void CancelTypingApp()
    {
        IsTypingApp = false;
        TypedApp = "";
    }

    private void SaveBlockedApps()
    {
        OnPropertyChanged(nameof(HasBlockedApps));
        OnPropertyChanged(nameof(HasNoBlockedApps));
        _setBlockedApps(BlockedApps.ToArray());
    }
}
