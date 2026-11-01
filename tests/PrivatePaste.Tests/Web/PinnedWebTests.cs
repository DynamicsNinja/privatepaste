using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Pages;

namespace PrivatePaste.Tests.Web;

public class PinnedWebTests
{
    private static readonly Regex TokenInput = new("<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ValueAttr = new("value=\"([^\"]+)\"", RegexOptions.Compiled);

    private static Task<HttpClient> WebLoginAsync(PrivatePasteFactory factory) => factory.LoginWebAsync();

    private static async Task<string> TokenFromAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var input = TokenInput.Match(html);
        Assert.True(input.Success, $"No antiforgery token on {path}");
        return ValueAttr.Match(input.Value).Groups[1].Value;
    }

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, string token,
        params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new("__RequestVerificationToken", token))));

    [Theory]
    [InlineData("/", "history")]
    [InlineData("/?tab=pinned", "pinned")]
    [InlineData("/?tab=PINNED&q=alpha", "pinned")]
    public async Task The_shell_tells_the_browser_which_tab_to_load(string path, string tab)
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);

        var html = await web.GetStringAsync(path);
        Assert.Matches($"<div id=\"history\" data-page=\"history\" data-tab=\"{tab}\"", html);
        var tabs = Regex.Match(html, "<div class=\"tabs\".*?</div>", RegexOptions.Singleline).Value;
        Assert.Single(Regex.Matches(tabs, "aria-current=\"page\""));
        Assert.Matches($"<a [^>]*aria-current=\"page\"[^>]*>{(tab == "pinned" ? "Pinned" : "History")}</a>", tabs);
    }

    [Fact]
    public async Task The_row_template_offers_pin_and_the_pinned_marker()
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);

        var row = Regex.Match(await web.GetStringAsync("/"), "<template id=\"clip-row\">(.*?)</template>",
            RegexOptions.Singleline).Groups[1].Value;
        Assert.Single(Regex.Matches(row, "class=\"pin-marker\""));
        Assert.Matches("<button type=\"button\" class=\"ghost pin-toggle\" aria-pressed=\"false\">.*?<span class=\"sr-only\">Pin</span></button>", row);
    }

    [Fact]
    public async Task Pin_and_unpin_through_the_api_with_a_cookie_toggle_the_clip()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        var clip = await factory.UploadTextAsync(api, "toggle me");
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/");
        HttpRequestMessage Request(HttpMethod method)
        {
            var request = new HttpRequestMessage(method, $"/api/clips/{clip.Id}/pin");
            request.Headers.Add("RequestVerificationToken", token);
            request.Headers.Add(ProtocolHeaders.Name, ProtocolHeaders.Version);
            return request;
        }

        Assert.Equal(HttpStatusCode.OK, (await web.SendAsync(Request(HttpMethod.Post))).StatusCode);
        var after = await api.GetFromJsonAsync<ClipDto>($"/api/clips/{clip.Id}", ProtocolJson.Options);
        Assert.NotNull(after!.PinnedAt);

        Assert.Equal(HttpStatusCode.OK, (await web.SendAsync(Request(HttpMethod.Delete))).StatusCode);
        after = await api.GetFromJsonAsync<ClipDto>($"/api/clips/{clip.Id}", ProtocolJson.Options);
        Assert.Null(after!.PinnedAt);
    }

    [Fact]
    public async Task Nav_has_a_hidden_lock_between_settings_and_sign_out()
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);
        var html = await web.GetStringAsync("/");
        var settings = html.IndexOf(">Settings</a>", StringComparison.Ordinal);
        var lockButton = html.IndexOf("<button type=\"button\" class=\"link\" id=\"lock\" hidden>Lock</button>", StringComparison.Ordinal);
        var signOut = html.IndexOf(">Sign out</button>", StringComparison.Ordinal);
        Assert.True(settings > 0 && settings < lockButton && lockButton < signOut);
    }

    [Fact]
    public async Task Nav_has_settings_between_people_and_sign_out()
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);
        var html = await web.GetStringAsync("/");
        var people = html.IndexOf(">People</a>", StringComparison.Ordinal);
        var settings = html.IndexOf(">Settings</a>", StringComparison.Ordinal);
        var signOut = html.IndexOf(">Sign out</button>", StringComparison.Ordinal);
        Assert.True(people > 0 && people < settings && settings < signOut);
    }

    [Fact]
    public async Task Settings_page_requires_sign_in()
    {
        using var factory = new PrivatePasteFactory();
        factory.Clock.SetUtcNow(DateTimeOffset.UtcNow);
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync("/settings")).StatusCode);
    }

    [Fact]
    public async Task Settings_page_shows_the_current_auto_delete_value()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        (await api.PutAsJsonAsync("/api/settings", new SettingsDto(30), ProtocolJson.Options)).EnsureSuccessStatusCode();
        var web = await WebLoginAsync(factory);

        var html = await web.GetStringAsync("/settings");
        Assert.Contains("Auto-delete history", html);
        Assert.Matches("<option value=\"30\" selected[^>]*>After 30 days</option>", html);
        Assert.DoesNotMatch("<option value=\"\" selected", html);
        Assert.Contains(">Off</option>", html);
        Assert.Contains(">After 7 days</option>", html);
        Assert.Contains(">After 90 days</option>", html);
    }

    [Fact]
    public async Task Settings_page_renders_Off_with_an_empty_value_and_selects_it_when_auto_delete_is_off()
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);

        var html = await web.GetStringAsync("/settings");
        Assert.Matches("<option value=\"\" selected[^>]*>Off</option>", html);
        Assert.DoesNotMatch("<option value=\"[0-9]+\" selected", html);
    }

    [Fact]
    public async Task Settings_page_asks_before_deleting_the_unpinned_count()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        await factory.UploadTextAsync(api, "one");
        await factory.UploadTextAsync(api, "two");
        var keep = await factory.UploadTextAsync(api, "three");
        await api.PostAsync($"/api/clips/{keep.Id}/pin", null);
        var web = await WebLoginAsync(factory);

        var html = await web.GetStringAsync("/settings");
        Assert.Contains("Delete 2 clips from every device? Pinned clips stay.", html);
        Assert.Contains("Clear history", html);
    }

    [Fact]
    public async Task Clear_history_post_deletes_unpinned_clips_only()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        var gone = await factory.UploadTextAsync(api, "to clear");
        var keep = await factory.UploadTextAsync(api, "to keep");
        await api.PostAsync($"/api/clips/{keep.Id}/pin", null);
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/settings");

        var response = await PostFormAsync(web, "/settings?handler=Clear", token);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/clips/{gone.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/api/clips/{keep.Id}")).StatusCode);
    }

    [Fact]
    public async Task Auto_delete_post_saves_the_setting()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/settings");

        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(web, "/settings?handler=AutoDelete", token, ("days", "7"))).StatusCode);
        Assert.Equal(7, (await api.GetFromJsonAsync<SettingsDto>("/api/settings", ProtocolJson.Options))!.AutoDeleteDays);

        // Post the value the page's own "Off" option carries, not a hand-written one.
        var off = Regex.Match(await web.GetStringAsync("/settings"), "<option value=\"([^\"]*)\"[^>]*>Off</option>");
        Assert.True(off.Success);
        Assert.Equal("", off.Groups[1].Value);
        await PostFormAsync(web, "/settings?handler=AutoDelete", token, ("days", off.Groups[1].Value));
        Assert.Null((await api.GetFromJsonAsync<SettingsDto>("/api/settings", ProtocolJson.Options))!.AutoDeleteDays);
    }

    [Fact]
    public void Pin_icon_matches_the_branding_svg()
    {
        var svg = XDocument.Load(Path.Combine(PlatformIconsTests.RepoRoot(), "branding", "ui-icons", "pin.svg")).Root!;
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal("0 0 24 24", (string?)svg.Attribute("viewBox"));
        Assert.Equal("2", (string?)svg.Attribute("stroke-width"));
        Assert.Equal(UiIcons.PinPath, (string?)svg.Element(ns + "path")?.Attribute("d"));
    }
}
