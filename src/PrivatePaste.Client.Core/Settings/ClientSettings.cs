namespace PrivatePaste.Client.Core.Settings;

public sealed record ClientSettings(
    string? ServerUrl = null,
    string DeviceName = "",
    string Hotkey = "Ctrl+Shift+V",
    bool Autostart = true,
    bool Paused = false,
    Guid? DeviceId = null,
    // A settings file from before this key existed loads as true (the constructor default).
    bool NotifyOnClipArrival = true);
