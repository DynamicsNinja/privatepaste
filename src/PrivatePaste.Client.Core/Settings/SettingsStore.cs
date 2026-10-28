using System.Text.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Settings;

public sealed class SettingsStore(string directory)
{
    private string FilePath => Path.Combine(directory, "settings.json");

    public ClientSettings Load()
    {
        if (!File.Exists(FilePath)) return new ClientSettings();
        try
        {
            var s = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath), ProtocolJson.Options);
            if (s is not null) return s;
        }
        catch (JsonException) { }
        File.Move(FilePath, FilePath + ".bad", overwrite: true);
        return new ClientSettings();
    }

    public void Save(ClientSettings settings)
    {
        Directory.CreateDirectory(directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, ProtocolJson.Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public static class AppPaths
{
    public static string DataDirectory { get; } = Resolve();

    private static string Resolve()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrivatePaste");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "PrivatePaste");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "privatepaste");
    }
}
