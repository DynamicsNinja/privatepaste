using System.Text.RegularExpressions;
using System.Xml.Linq;
using PrivatePaste.Server.Pages;

namespace PrivatePaste.Tests.Web;

public class RowActionsWebTests
{
    // Rows are built in the browser from this template, so the buttons' names and icons are checked here.
    private static async Task<string> RowHtmlAsync()
    {
        using var factory = new PrivatePasteFactory();
        var web = await factory.LoginWebAsync();
        var html = await web.GetStringAsync("/");
        var row = Regex.Match(html, "<template id=\"clip-row\">\\s*(<li class=\"clip\".*?</li>)", RegexOptions.Singleline);
        Assert.True(row.Success, "No clip row template on the history page");
        return row.Groups[1].Value;
    }

    private static string Button(string row, string cssClass)
    {
        var button = Regex.Match(row, $"<button[^>]*class=\"[^\"]*\\b{cssClass}\\b[^\"]*\"[^>]*>.*?</button>",
            RegexOptions.Singleline);
        Assert.True(button.Success, $"No .{cssClass} button in the row");
        return button.Value;
    }

    /// <summary>Text between tags, ignoring the screen-reader-only label and the icons.</summary>
    private static string VisibleText(string button) =>
        Regex.Replace(Regex.Replace(button, "<span class=\"sr-only\">.*?</span>|<svg.*?</svg>", "",
            RegexOptions.Singleline), "<[^>]+>", "").Trim();

    [Fact]
    public async Task Copy_is_an_icon_button_named_copy()
    {
        var copy = Button(await RowHtmlAsync(), "copy");
        Assert.Contains("aria-label=\"Copy\"", copy);
        Assert.Contains("title=\"Copy\"", copy);
        Assert.Contains(UiIcons.CopyPath, copy);
        Assert.Contains(UiIcons.CheckPath, copy); // shown once the copy has worked
        Assert.Equal("", VisibleText(copy));
    }

    [Fact]
    public async Task Delete_is_an_icon_button_named_delete()
    {
        var delete = Button(await RowHtmlAsync(), "delete");
        Assert.Contains("aria-label=\"Delete\"", delete);
        Assert.Contains("title=\"Delete\"", delete);
        Assert.Contains(UiIcons.TrashPath, delete);
        Assert.Equal("", VisibleText(delete));
    }

    [Fact]
    public async Task File_lines_have_a_download_button_and_kind_icon()
    {
        using var factory = new PrivatePasteFactory();
        var web = await factory.LoginWebAsync();
        var html = await web.GetStringAsync("/");
        var line = Regex.Match(html, "<template id=\"file-line\">.*?</template>", RegexOptions.Singleline);
        Assert.True(line.Success, "No file line template on the history page");
        var download = Button(line.Value, "file-download"); // JS fills in "Download {name}"
        Assert.Contains(UiIcons.DownloadPath, download);
        Assert.Equal("", VisibleText(download));
        var kinds = Regex.Match(html, "<template id=\"kind-icons\">.*?</template>", RegexOptions.Singleline).Value;
        Assert.Contains("data-kind=\"file\"", kinds);
        Assert.Contains("M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z M14 3v5h5", kinds);
    }

    [Theory]
    [InlineData("copy.svg")]
    [InlineData("trash.svg")]
    [InlineData("check.svg")]
    [InlineData("download.svg")]
    public void Icon_matches_the_branding_svg(string file)
    {
        var svg = XDocument.Load(Path.Combine(PlatformIconsTests.RepoRoot(), "branding", "ui-icons", file)).Root!;
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal("0 0 24 24", (string?)svg.Attribute("viewBox"));
        Assert.Equal("2", (string?)svg.Attribute("stroke-width"));
        var expected = file switch
        {
            "copy.svg" => UiIcons.CopyPath,
            "trash.svg" => UiIcons.TrashPath,
            "download.svg" => UiIcons.DownloadPath,
            _ => UiIcons.CheckPath,
        };
        Assert.Equal(expected, (string?)svg.Element(ns + "path")?.Attribute("d"));
    }
}
