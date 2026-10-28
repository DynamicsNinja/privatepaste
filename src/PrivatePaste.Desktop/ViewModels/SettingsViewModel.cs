using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivatePaste.Desktop.Input;

namespace PrivatePaste.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    public const string InvalidHotkeyMessage = "Use a combination like Ctrl+Shift+V.";

    private readonly Action<HotkeyGesture> _applyHotkey;
    private readonly Action<bool> _setAutostart;
    private readonly Func<Task> _signOut;
    private readonly Action<bool> _setNotifyOnClipArrival;
    private string _appliedHotkey;

    public SettingsViewModel(
        string server, string deviceName, string hotkey, bool autostart, string autostartLabel,
        Action<HotkeyGesture> applyHotkey, Action<bool> setAutostart, Func<Task> signOut,
        bool notifyOnClipArrival = true, Action<bool>? setNotifyOnClipArrival = null)
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
}
