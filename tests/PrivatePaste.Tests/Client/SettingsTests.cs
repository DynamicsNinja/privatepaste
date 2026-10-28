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
    }

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
