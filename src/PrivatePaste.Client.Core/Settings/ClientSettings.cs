namespace PrivatePaste.Client.Core.Settings;

public sealed record ClientSettings(
    string? ServerUrl = null,
    string DeviceName = "",
    string Hotkey = "Ctrl+Shift+V",
    bool Autostart = true,
    bool Paused = false,
    Guid? DeviceId = null,
    // A settings file from before this key existed loads as true (the constructor default).
    bool NotifyOnClipArrival = true)
{
    /// <summary>
    /// Apps whose copies are never synced (see <see cref="PrivatePaste.Client.Core.Settings.BlockedApps.Matches"/>).
    /// A settings file from before this key existed, or with null, loads as empty. An empty list is always the shared empty
    /// array, so default settings still compare equal (non-empty lists compare by reference, as records do).
    /// </summary>
    public IReadOnlyList<string> BlockedApps
    {
        get;
        init => field = value is { Count: > 0 } ? value : [];
    } = [];
}
