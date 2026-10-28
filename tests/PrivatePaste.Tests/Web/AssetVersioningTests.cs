namespace PrivatePaste.Tests.Web;

// The site sits behind a Cloudflare cache that keeps static files for hours; every asset URL must change when the file
// changes, or a deploy keeps showing the old look.
public class AssetVersioningTests
{
    [Theory]
    [InlineData("/site.css?v=")]
    [InlineData("/site.js?v=")]
    [InlineData("/logo.svg?v=")]
    public async Task Pages_reference_versioned_assets(string expected)
    {
        using var factory = new PrivatePasteFactory();
        var html = await factory.CreateClient().GetStringAsync("/login");
        Assert.Contains(expected, html);
    }

    [Fact]
    public async Task Stylesheet_references_versioned_fonts()
    {
        using var factory = new PrivatePasteFactory();
        var css = await factory.CreateClient().GetStringAsync("/site.css");
        Assert.Contains("/fonts/sora-latin.woff2?v=", css);
        Assert.Contains("/fonts/sora-latin-ext.woff2?v=", css);
    }
}
