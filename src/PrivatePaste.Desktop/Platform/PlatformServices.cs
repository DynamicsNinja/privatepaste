using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Desktop.Platform.Linux;
using PrivatePaste.Desktop.Platform.Mac;
using PrivatePaste.Desktop.Platform.Polling;
using PrivatePaste.Desktop.Platform.Windows;

namespace PrivatePaste.Desktop.Platform;

/// <summary>The OS-specific services, chosen once at startup. Each OS has its own section in <see cref="Create"/>.</summary>
public sealed class PlatformServices
{
    public const string WaylandNotice = "Clipboard sync on Wayland is limited; X11 sessions work best.";

    public required IClipboardAccess Clipboard { get; init; }
    public required IAutostart Autostart { get; init; }
    public required ISecretProtector SecretProtector { get; init; }

    /// <summary>The <c>platform</c> sent at login: "windows", "macos" or "linux".</summary>
    public required string PlatformName { get; init; }

    /// <summary>Settings label for the autostart checkbox.</summary>
    public required string AutostartLabel { get; init; }

    /// <param name="exePath">The executable that autostart launches.</param>
    /// <param name="isolated">True for dev/test runs with a custom data directory: autostart becomes a no-op.</param>
    /// <param name="notifier">Used for one-time notices (Linux key-file fallback, Wayland). Optional.</param>
    /// <param name="dataDirectory">Where the Linux key-file fallback and the Wayland "shown" marker live.</param>
    public static PlatformServices Create(string exePath, bool isolated, INotifier? notifier = null, string? dataDirectory = null)
    {
        // ---- Windows ----
        if (OperatingSystem.IsWindows())
        {
            return new PlatformServices
            {
                Clipboard = new WindowsClipboard(msg => App.Log?.Info(msg)),
                Autostart = isolated ? new NullAutostart() : new WindowsAutostart(exePath),
                SecretProtector = new DpapiSecretProtector(),
                PlatformName = "windows",
                AutostartLabel = "Start with Windows",
            };
        }

        // ---- macOS ----
        if (OperatingSystem.IsMacOS())
        {
            return new PlatformServices
            {
                Clipboard = AvaloniaPollingClipboard.Create(),
                Autostart = isolated ? new NullAutostart() : new MacAutostart(exePath),
                SecretProtector = new KeychainSecretProtector(),
                PlatformName = "macos",
                AutostartLabel = "Start at login",
            };
        }

        // ---- Linux ----
        if (OperatingSystem.IsLinux())
        {
            var dir = dataDirectory ?? Client.Core.Settings.AppPaths.DataDirectory;
            notifier ??= new SilentNotifier();
            var services = new PlatformServices
            {
                Clipboard = AvaloniaPollingClipboard.Create(),
                Autostart = isolated ? new NullAutostart() : new XdgAutostart(exePath),
                SecretProtector = new SecretToolProtector(Path.Combine(dir, "data-key.bin"), notifier),
                PlatformName = "linux",
                AutostartLabel = "Start at login",
            };
            if (IsWayland(Environment.GetEnvironmentVariable)) NotifyOnce(notifier, Path.Combine(dir, "wayland-notice-shown"), WaylandNotice);
            return services;
        }

        throw new PlatformNotSupportedException("PrivatePaste does not support this operating system yet.");
    }

    /// <summary>Wayland session when <c>WAYLAND_DISPLAY</c> is set.</summary>
    public static bool IsWayland(Func<string, string?> env) => !string.IsNullOrEmpty(env("WAYLAND_DISPLAY"));

    private static void NotifyOnce(INotifier notifier, string marker, string message)
    {
        try
        {
            if (File.Exists(marker)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "");
        }
        catch (IOException) { }   // cannot persist the marker: showing the notice again next start is fine
        catch (UnauthorizedAccessException) { }
        notifier.Notify(message);
    }

    private sealed class SilentNotifier : INotifier
    {
        public void Notify(string message) { }
    }
}
