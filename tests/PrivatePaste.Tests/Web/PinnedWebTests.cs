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

    private static async Task<ClipDto> UploadAsync(HttpClient api, string text)
    {
        var response = await api.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, false), ProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    [Fact]
    public async Task Pinned_tab_lists_only_pinned_clips()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        await UploadAsync(api, "plain-clip-text");
        var pinned = await UploadAsync(api, "pinned-clip-text");
        (await api.PostAsync($"/api/clips/{pinned.Id}/pin", null)).EnsureSuccessStatusCode();
        var web = await WebLoginAsync(factory);

        var tab = await web.GetStringAsync("/?tab=pinned");
        Assert.Contains("pinned-clip-text", tab);
        Assert.DoesNotContain("plain-clip-text", tab);

        var history = await web.GetStringAsync("/");
        Assert.Contains("plain-clip-text", history);
        Assert.Contains("pinned-clip-text", history);
    }

    [Fact]
    public async Task Pinned_tab_search_applies_to_pinned_clips()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        foreach (var text in new[] { "alpha one", "beta two" })
            (await api.PostAsync($"/api/clips/{(await UploadAsync(api, text)).Id}/pin", null)).EnsureSuccessStatusCode();
        var web = await WebLoginAsync(factory);

        var html = await web.GetStringAsync("/?tab=pinned&q=alpha");
        Assert.Contains("alpha one", html);
        Assert.DoesNotContain("beta two", html);
    }

    [Fact]
    public async Task History_marks_pinned_clips_and_offers_pin_or_unpin()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        await UploadAsync(api, "loose clip");
        var pinned = await UploadAsync(api, "kept clip");
        await api.PostAsync($"/api/clips/{pinned.Id}/pin", null);
        var web = await WebLoginAsync(factory);

        var html = await web.GetStringAsync("/");
        Assert.Single(Regex.Matches(html, "class=\"pin-marker\""));
        Assert.Contains(">Pin</span>", html);
        Assert.Contains(">Unpin</span>", html);
    }

    [Fact]
    public async Task Pin_and_unpin_from_web_toggle_the_clip()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        var clip = await UploadAsync(api, "toggle me");
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/");

        var pin = await PostFormAsync(web, "/?handler=Pin", token, ("id", clip.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, pin.StatusCode);
        var after = await api.GetFromJsonAsync<ClipDto>($"/api/clips/{clip.Id}", ProtocolJson.Options);
        Assert.NotNull(after!.PinnedAt);

        var unpin = await PostFormAsync(web, "/?handler=Unpin", token, ("id", clip.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, unpin.StatusCode);
        after = await api.GetFromJsonAsync<ClipDto>($"/api/clips/{clip.Id}", ProtocolJson.Options);
        Assert.Null(after!.PinnedAt);
    }

    [Fact]
    public async Task Nav_has_settings_between_devices_and_sign_out()
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);
        var html = await web.GetStringAsync("/");
        var devices = html.IndexOf(">Devices</a>", StringComparison.Ordinal);
        var settings = html.IndexOf(">Settings</a>", StringComparison.Ordinal);
        var signOut = html.IndexOf(">Sign out</button>", StringComparison.Ordinal);
        Assert.True(devices > 0 && devices < settings && settings < signOut);
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
        await UploadAsync(api, "one");
        await UploadAsync(api, "two");
        var keep = await UploadAsync(api, "three");
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
        var gone = await UploadAsync(api, "to clear");
        var keep = await UploadAsync(api, "to keep");
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
