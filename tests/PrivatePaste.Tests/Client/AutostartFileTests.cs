using PrivatePaste.Desktop.Platform.Linux;
using PrivatePaste.Desktop.Platform.Mac;

namespace PrivatePaste.Tests.Client;

public class AutostartFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-autostart-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Plist_has_label_quoted_program_arguments_minimized_and_run_at_load()
    {
        var plist = MacAutostart.BuildPlist("/Applications/Private Paste.app/Contents/MacOS/PrivatePaste");
        Assert.Contains("<key>Label</key>", plist);
        Assert.Contains("<string>xyz.ficdev.privatepaste</string>", plist);
        Assert.Contains("<key>ProgramArguments</key>", plist);
        Assert.Contains("<string>/Applications/Private Paste.app/Contents/MacOS/PrivatePaste</string>", plist);
        Assert.Contains("<string>--minimized</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.Contains("<true/>", plist);
    }

    [Fact]
    public void Plist_escapes_xml_in_the_path()
    {
        var plist = MacAutostart.BuildPlist("/Users/a&b/<x>/PrivatePaste");
        Assert.Contains("<string>/Users/a&amp;b/&lt;x&gt;/PrivatePaste</string>", plist);
    }

    [Fact]
    public void Plist_is_well_formed_xml()
    {
        var doc = System.Xml.Linq.XDocument.Parse(MacAutostart.BuildPlist("/a b/PrivatePaste"));
        Assert.Equal("plist", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void Desktop_entry_quotes_exec_and_has_minimized_and_gnome_flag()
    {
        var text = XdgAutostart.BuildDesktopEntry("/opt/Private Paste/PrivatePaste");
        Assert.Contains("[Desktop Entry]", text);
        Assert.Contains("Type=Application", text);
        Assert.Contains("Name=PrivatePaste", text);
        Assert.Contains("Exec=\"/opt/Private Paste/PrivatePaste\" --minimized", text);
        Assert.Contains("X-GNOME-Autostart-enabled=true", text);
    }

    [Fact]
    public void Desktop_entry_escapes_special_characters_in_exec()
    {
        // Desktop Entry spec: inside a quoted Exec argument, " ` $ \ are backslash-escaped, and the string
        // value is itself unescaped once more, so each of those gets a doubled backslash. A literal % is %%.
        var text = XdgAutostart.BuildDesktopEntry("/opt/we\"ird$/100%/PrivatePaste");
        Assert.Contains("Exec=\"/opt/we\\\\\"ird\\\\$/100%%/PrivatePaste\" --minimized", text);
    }

    [Fact]
    public void Mac_autostart_writes_and_removes_the_plist()
    {
        var path = Path.Combine(_dir, "x.plist");
        var a = new MacAutostart("/x/PrivatePaste", path);
        Assert.False(a.IsEnabled);
        a.Set(true);
        Assert.True(a.IsEnabled);
        Assert.Contains("--minimized", File.ReadAllText(path));
        a.Set(false);
        Assert.False(a.IsEnabled);
    }

    [Fact]
    public void Xdg_autostart_writes_and_removes_the_desktop_file()
    {
        var path = Path.Combine(_dir, "autostart", "privatepaste.desktop");
        var a = new XdgAutostart("/x/PrivatePaste", path);
        Assert.False(a.IsEnabled);
        a.Set(true);
        Assert.True(a.IsEnabled);
        Assert.Contains("X-GNOME-Autostart-enabled=true", File.ReadAllText(path));
        a.Set(false);
        Assert.False(a.IsEnabled);
    }
}
