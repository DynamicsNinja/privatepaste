using System.Text;

namespace PrivatePaste.Desktop.Platform.Linux;

/// <summary>XDG autostart: <c>~/.config/autostart/privatepaste.desktop</c> (honours <c>XDG_CONFIG_HOME</c>).</summary>
public sealed class XdgAutostart(string exePath, string? desktopFilePath = null) : IAutostart
{
    private readonly string _path = desktopFilePath ?? DefaultPath();

    public bool IsEnabled => File.Exists(_path);

    public void Set(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, BuildDesktopEntry(exePath));
        }
        else if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static string DefaultPath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var config = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(config, "autostart", "privatepaste.desktop");
    }

    public static string BuildDesktopEntry(string exePath) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=PrivatePaste\n" +
        "Comment=Private clipboard sync\n" +
        $"Exec={QuoteExec(exePath)} --minimized\n" +
        "Terminal=false\n" +
        "X-GNOME-Autostart-enabled=true\n";

    /// <summary>
    /// Desktop Entry spec: quote the argument; inside quotes <c>"</c> <c>`</c> <c>$</c> <c>\</c> are backslash-escaped, and
    /// because the whole value is also unescaped as a string, each backslash is doubled. A literal % is %%.
    /// </summary>
    private static string QuoteExec(string path)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append("\\\\");
            if (c == '%') sb.Append('%');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
