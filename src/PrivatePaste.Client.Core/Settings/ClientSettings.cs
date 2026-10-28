namespace PrivatePaste.Client.Core.Settings;

public sealed record ClientSettings(
    string? ServerUrl = null,
    string DeviceName = "",
    string Hotkey = "Ctrl+Shift+V",
    bool Autostart = true,
    bool Paused = false,
    Guid? DeviceId = null);
