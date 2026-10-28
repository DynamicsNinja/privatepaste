using System.Security;

namespace PrivatePaste.Desktop.Platform.Mac;

/// <summary>A LaunchAgent: <c>~/Library/LaunchAgents/xyz.ficdev.privatepaste.plist</c> running the app with <c>--minimized</c> at login.</summary>
public sealed class MacAutostart(string exePath, string? plistPath = null) : IAutostart
{
    public const string Label = "xyz.ficdev.privatepaste";

    private readonly string _path = plistPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public bool IsEnabled => File.Exists(_path);

    public void Set(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, BuildPlist(exePath));
        }
        else if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    public static string BuildPlist(string exePath) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\">\n" +
        "<dict>\n" +
        "    <key>Label</key>\n" +
        $"    <string>{Label}</string>\n" +
        "    <key>ProgramArguments</key>\n" +
        "    <array>\n" +
        $"        <string>{SecurityElement.Escape(exePath)}</string>\n" +
        "        <string>--minimized</string>\n" +
        "    </array>\n" +
        "    <key>RunAtLoad</key>\n" +
        "    <true/>\n" +
        "</dict>\n" +
        "</plist>\n";
}
