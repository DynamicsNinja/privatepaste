using PrivatePaste.Client.Core.Settings;

namespace PrivatePaste.Tests.Client;

public class SettingsTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "pp-settings-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("https://paste.ficdev.xyz/", "https://paste.ficdev.xyz/")]
    [InlineData(" https://x.io/sub/ ", "https://x.io/sub")]
    [InlineData("http://localhost:5299", "http://localhost:5299/")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
    public void Normalizes(string input, string expected) =>
        Assert.Equal(expected, ServerUrl.Normalize(input).ToString());

    [Theory]
    [InlineData("http://paste.ficdev.xyz")]
    [InlineData("ftp://x.io")]
    [InlineData("paste.ficdev.xyz")]
    [InlineData("")]
    public void Rejects(string input) => Assert.Throws<ArgumentException>(() => ServerUrl.Normalize(input));

    [Fact]
    public void Accepts_ipv6_loopback_over_http() =>
        Assert.Equal("http://[::1]:5299/", ServerUrl.Normalize("http://[::1]:5299").ToString());

    [Fact]
    public void Defaults_when_missing()
    {
        var s = new SettingsStore(TempDir()).Load();
        Assert.True(s.Autostart);
        Assert.Equal("Ctrl+Shift+V", s.Hotkey);
        Assert.False(s.Paused);
        Assert.True(s.NotifyOnClipArrival);
    }

    [Fact]
    public void Old_settings_file_without_the_notify_key_loads_with_notifications_on()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{"serverUrl":"https://x.io/","deviceName":"Laptop","hotkey":"Ctrl+Shift+V","autostart":true,"paused":false,"deviceId":null}""");
        var s = new SettingsStore(dir).Load();
        Assert.Equal("Laptop", s.DeviceName);
        Assert.True(s.NotifyOnClipArrival);
    }

    [Fact]
    public void Settings_OldFileWithoutUsername_LoadsEmpty()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"serverUrl":"https://x.io/","deviceName":"Laptop"}""");
        var store = new SettingsStore(dir);
        Assert.Equal("", store.Load().Username);
        store.Save(store.Load() with { Username = "Ana" });
        Assert.Equal("Ana", new SettingsStore(dir).Load().Username);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Notify_on_clip_arrival_roundtrips(bool value)
    {
        var store = new SettingsStore(TempDir());
        store.Save(new ClientSettings(DeviceName: "Laptop", NotifyOnClipArrival: value));
        Assert.Equal(value, store.Load().NotifyOnClipArrival);
    }

    [Fact]
    public void Blocked_apps_roundtrip()
    {
        var store = new SettingsStore(TempDir());
        store.Save(new ClientSettings(DeviceName: "Laptop") { BlockedApps = ["keepass", "com.agilebits.onepassword7"] });
        var loaded = store.Load();
        Assert.Equal(["keepass", "com.agilebits.onepassword7"], loaded.BlockedApps);
        Assert.Equal("Laptop", loaded.DeviceName);
    }

    [Fact]
    public void Old_settings_file_without_blocked_apps_loads_an_empty_list()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{"serverUrl":"https://x.io/","deviceName":"Laptop","hotkey":"Ctrl+Shift+V","autostart":true,"paused":false,"deviceId":null,"notifyOnClipArrival":false}""");
        var s = new SettingsStore(dir).Load();
        Assert.Empty(s.BlockedApps);
        Assert.False(s.NotifyOnClipArrival);
        Assert.Empty(new ClientSettings().BlockedApps);

        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"deviceName":"Laptop","blockedApps":null}""");
        Assert.Empty(new SettingsStore(dir).Load().BlockedApps);
    }

    [Theory]
    [InlineData("keepass", "keepass", true)]
    [InlineData("KeePass.exe", "keepass", true)]
    [InlineData(" KEEPASS ", "keepass", true)]
    [InlineData("com.agilebits.onepassword7", "com.agilebits.onepassword7", true)]
    [InlineData("keepass", "keepassxc", false)]
    [InlineData("", "keepass", false)]
    public void Blocked_apps_match_case_insensitively(string entry, string sourceApp, bool blocked) =>
        Assert.Equal(blocked, BlockedApps.Matches([entry], sourceApp));

    [Fact]
    public void Roundtrip()
    {
        var store = new SettingsStore(TempDir());
        var s = new ClientSettings("https://x.io/", "Laptop", "Ctrl+Alt+V", false, true, Guid.NewGuid());
        store.Save(s);
        Assert.Equal(s, store.Load());
    }

    [Fact]
    public void Corrupt_file_yields_defaults_and_is_kept_aside()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{not json");
        var s = new SettingsStore(dir).Load();
        Assert.Equal(new ClientSettings(), s);
        Assert.True(File.Exists(Path.Combine(dir, "settings.json.bad")));
        Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
    }
}
