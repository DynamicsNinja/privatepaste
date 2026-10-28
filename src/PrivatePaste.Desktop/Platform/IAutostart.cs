namespace PrivatePaste.Desktop.Platform;

/// <summary>Starts the app (with <c>--minimized</c>) when the user logs in.</summary>
public interface IAutostart
{
    bool IsEnabled { get; }
    void Set(bool enabled);
}

/// <summary>Does nothing. Used for dev/test runs with a custom data directory so the real login items are never touched.</summary>
public sealed class NullAutostart : IAutostart
{
    public bool IsEnabled { get; private set; }
    public void Set(bool enabled) => IsEnabled = enabled;
}
