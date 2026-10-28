using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Desktop.Platform.Windows;

namespace PrivatePaste.Desktop.Platform;

/// <summary>The OS-specific services, chosen once at startup. Each OS has its own section in <see cref="Create"/>.</summary>
public sealed class PlatformServices
{
    public required IClipboardAccess Clipboard { get; init; }
    public required IAutostart Autostart { get; init; }
    public required ISecretProtector SecretProtector { get; init; }

    /// <summary>The <c>platform</c> sent at login: "windows", "macos" or "linux".</summary>
    public required string PlatformName { get; init; }

    /// <summary>Settings label for the autostart checkbox.</summary>
    public required string AutostartLabel { get; init; }

    /// <param name="exePath">The executable that autostart launches.</param>
    /// <param name="isolated">True for dev/test runs with a custom data directory: autostart becomes a no-op.</param>
    public static PlatformServices Create(string exePath, bool isolated)
    {
        // ---- Windows ----
        if (OperatingSystem.IsWindows())
        {
            return new PlatformServices
            {
                Clipboard = new NullClipboard(),   // Task 8: WindowsClipboard
                Autostart = isolated ? new NullAutostart() : new WindowsAutostart(exePath),
                SecretProtector = new DpapiSecretProtector(),
                PlatformName = "windows",
                AutostartLabel = "Start with Windows",
            };
        }

        // ---- macOS ---- (Task 11)
        // ---- Linux ---- (Task 11)
        throw new PlatformNotSupportedException("PrivatePaste does not support this operating system yet.");
    }
}
