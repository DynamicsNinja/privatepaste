using System.Text.RegularExpressions;

namespace PrivatePaste.Tests.Web;

public class FilterChipsWebTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/?tab=pinned")]
    public async Task The_history_shell_has_the_type_chips_in_order_with_All_pressed(string path)
    {
        using var factory = new PrivatePasteFactory();
        var web = await factory.LoginWebAsync();

        var html = await web.GetStringAsync(path);
        var bar = Regex.Match(html, "<div class=\"filters\"[^>]*>(.*?)</div>", RegexOptions.Singleline);
        Assert.True(bar.Success, "No filter bar");
        Assert.Matches("role=\"group\"[^>]*aria-label=\"Filter by type\"|aria-label=\"Filter by type\"[^>]*role=\"group\"", bar.Value);

        var chips = Regex.Matches(bar.Groups[1].Value, "<button type=\"button\" class=\"chip\" data-filter=\"([a-z]+)\" aria-pressed=\"(true|false)\">(.*?)</button>",
            RegexOptions.Singleline);
        Assert.Equal(new[] { "all", "text", "link", "image", "file" }, chips.Select(c => c.Groups[1].Value));
        Assert.Equal(new[] { "true", "false", "false", "false", "false" }, chips.Select(c => c.Groups[2].Value));
        Assert.Equal(new[] { "All", "Text", "Links", "Images", "Files" },
            chips.Select(c => Regex.Replace(c.Groups[3].Value, "<[^>]*>", "").Trim()));
    }

    [Fact]
    public async Task The_chips_sit_under_the_tabs_inside_the_clips_view()
    {
        using var factory = new PrivatePasteFactory();
        var web = await factory.LoginWebAsync();

        var html = await web.GetStringAsync("/");
        var view = html.IndexOf("id=\"clips-view\"", StringComparison.Ordinal);
        var tabs = html.IndexOf("class=\"tabs\"", StringComparison.Ordinal);
        var filters = html.IndexOf("class=\"filters\"", StringComparison.Ordinal);
        var clips = html.IndexOf("id=\"clips\"", StringComparison.Ordinal);
        Assert.True(view > 0 && view < tabs && tabs < filters && filters < clips);
    }

    [Fact]
    public async Task The_kind_icons_the_chips_reuse_are_all_in_the_template()
    {
        using var factory = new PrivatePasteFactory();
        var web = await factory.LoginWebAsync();

        var icons = Regex.Match(await web.GetStringAsync("/"), "<template id=\"kind-icons\">(.*?)</template>", RegexOptions.Singleline).Groups[1].Value;
        foreach (var kind in new[] { "text", "link", "image", "file" })
            Assert.Contains($"data-kind=\"{kind}\"", icons);
    }
}
