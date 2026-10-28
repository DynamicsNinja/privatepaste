using System.Xml.Linq;
using PrivatePaste.Server.Pages;

namespace PrivatePaste.Tests.Web;

public class PlatformIconsTests
{
    [Theory]
    [InlineData("windows", "windows", "Windows")]
    [InlineData("macos", "macos", "macOS")]
    [InlineData("linux", "linux", "Linux")]
    [InlineData("android", "android", "Android")]
    [InlineData("web", "web", "Web")]
    [InlineData("Android", "android", "Android")]
    [InlineData("unknown", "unknown", "Unknown device")]
    [InlineData("freebsd", "unknown", "Unknown device")]
    [InlineData("", "unknown", "Unknown device")]
    [InlineData(null, "unknown", "Unknown device")]
    public void Maps_platforms_to_icons(string? platform, string key, string label)
    {
        var icon = PlatformIcons.For(platform);
        Assert.Equal(key, icon.Key);
        Assert.Equal(label, icon.Label);
    }

    /// <summary>The server's copy of the icons must match the branding SVGs they were taken from.</summary>
    [Fact]
    public void Icons_match_the_branding_svgs()
    {
        var dir = Path.Combine(RepoRoot(), "branding", "platform-icons");
        var files = Directory.GetFiles(dir, "*.svg").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        Assert.Equal(PlatformIcons.All.Select(i => i.Key).Order().ToArray(), files);

        foreach (var icon in PlatformIcons.All)
        {
            var svg = XDocument.Load(Path.Combine(dir, icon.Key + ".svg")).Root!;
            XNamespace ns = "http://www.w3.org/2000/svg";
            Assert.Equal("0 0 24 24", (string?)svg.Attribute("viewBox"));
            Assert.Equal("currentColor", (string?)svg.Attribute("stroke"));
            Assert.Equal(icon.Label, (string?)svg.Attribute("aria-label"));
            Assert.Equal(icon.Label, svg.Element(ns + "title")?.Value);
            Assert.Equal(icon.PathData, (string?)svg.Element(ns + "path")?.Attribute("d"));
        }
    }

    [Fact]
    public void Svg_is_labelled_and_uses_current_colour()
    {
        var html = PlatformIcons.Svg("linux").ToString();
        Assert.Contains("role=\"img\" aria-label=\"Linux\"", html);
        Assert.Contains("stroke=\"currentColor\"", html);
        Assert.Contains("<title>Linux</title>", html);
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PrivatePaste.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
