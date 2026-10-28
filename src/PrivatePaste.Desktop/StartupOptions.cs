using System.Security.Cryptography;
using System.Text;
using PrivatePaste.Client.Core.Settings;

namespace PrivatePaste.Desktop;

/// <summary>
/// Command line: <c>--minimized</c> (autostart; the app always starts in the tray once signed in) and
/// <c>--data-dir &lt;path&gt;</c> (or env <c>PRIVATEPASTE_DATA_DIR</c>) for dev/test runs. A custom data directory makes the
/// run isolated: its own single-instance mutex and no changes to the OS login items.
/// </summary>
public sealed record StartupOptions(string DataDirectory, bool Minimized, bool Isolated)
{
    public const string DataDirEnvironmentVariable = "PRIVATEPASTE_DATA_DIR";

    public static StartupOptions Parse(string[] args, Func<string, string?> env)
    {
        var minimized = false;
        string? dataDir = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--minimized":
                    minimized = true;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
            }
        }
        dataDir ??= env(DataDirEnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : null;
        return dataDir is null
            ? new StartupOptions(AppPaths.DataDirectory, minimized, Isolated: false)
            : new StartupOptions(Path.GetFullPath(dataDir), minimized, Isolated: true);
    }

    /// <summary><c>PrivatePaste.SingleInstance.{username}</c>, plus a data-directory hash for isolated runs.</summary>
    public string MutexName
    {
        get
        {
            var name = $"PrivatePaste.SingleInstance.{Environment.UserName}";
            if (!Isolated) return name;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant()));
            return name + "." + Convert.ToHexString(hash, 0, 8);
        }
    }
}
