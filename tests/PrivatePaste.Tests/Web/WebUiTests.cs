using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Web;

public class WebUiTests
{
    private static readonly Regex TokenInput = new("<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ValueAttr = new("value=\"([^\"]+)\"", RegexOptions.Compiled);

    private static HttpClient WebClient(PrivatePasteFactory factory)
    {
        // The test client's cookie jar uses the real clock; the server's fake clock would issue already-expired cookies.
        factory.Clock.SetUtcNow(DateTimeOffset.UtcNow);
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

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

    private static Task<HttpClient> WebLoginAsync(PrivatePasteFactory factory) => factory.LoginWebAsync();

    private static HttpRequestMessage ApiPost(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("RequestVerificationToken", token);
        return request;
    }

    [Fact]
    public async Task Index_redirects_to_login_when_anonymous()
    {
        using var factory = new PrivatePasteFactory();
        var response = await WebClient(factory).GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task Login_wrong_password_shows_error()
    {
        using var factory = new PrivatePasteFactory();
        var client = WebClient(factory);
        var token = await TokenFromAsync(client, "/login");
        var response = await PostFormAsync(client, "/login", token, ("Name", "owner"), ("Password", "nope"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Wrong name or password.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_is_rate_limited()
    {
        using var factory = new PrivatePasteFactory();
        var client = WebClient(factory);
        var token = await TokenFromAsync(client, "/login");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostFormAsync(client, "/login", token, ("Name", "owner"), ("Password", "nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostFormAsync(client, "/login", token, ("Name", "owner"), ("Password", "nope"))).StatusCode);
        // Viewing the page is never limited.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/login")).StatusCode);
    }

    [Fact]
    public async Task Login_cookie_is_httponly_and_samesite_strict()
    {
        using var factory = new PrivatePasteFactory();
        var client = WebClient(factory);
        var token = await TokenFromAsync(client, "/login");
        var response = await PostFormAsync(client, "/login", token, ("Name", "owner"), ("Password", PrivatePasteFactory.Password));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Cookies="));
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Index_RendersShellWithoutClipData()
    {
        using var factory = new PrivatePasteFactory();
        var clip = await factory.UploadTextAsync(await factory.LoginAsync(), "hello web");
        var web = await WebLoginAsync(factory);
        var response = await web.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // The browser fetches and decrypts the clips; the page itself carries none of them.
        Assert.Contains("data-page=\"history\"", html);
        Assert.DoesNotContain(clip.Id.ToString(), html);
        Assert.DoesNotContain(clip.Payload!, html);
        Assert.DoesNotContain(clip.Hash, html);
    }

    [Fact]
    public async Task Index_HasNoServerPinHandlers()
    {
        using var factory = new PrivatePasteFactory();
        var clip = await factory.UploadTextAsync(await factory.LoginAsync(), "pin me");
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/");
        foreach (var handler in new[] { "Pin", "Unpin", "Delete" })
        {
            var response = await PostFormAsync(web, $"/?handler={handler}", token, ("id", clip.Id.ToString()));
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                $"POST /?handler={handler} gave {(int)response.StatusCode}");
        }
        var after = await (await factory.LoginAsync()).GetFromJsonAsync<ClipDto>($"/api/clips/{clip.Id}", ProtocolJson.Options);
        Assert.Null(after!.PinnedAt);
    }

    [Fact]
    public async Task Index_ships_the_shell_the_browser_fills()
    {
        using var factory = new PrivatePasteFactory();
        var html = await (await WebLoginAsync(factory)).GetStringAsync("/");
        Assert.Contains("Unlock your history", html);
        Assert.Contains("Set up encryption", html);
        Assert.Contains("Repeat passphrase", html);
        Assert.Contains("Use at least 12 characters. A short sentence works well.", html);
        Assert.Contains("Ask the owner to set up encryption.", html);
        Assert.Contains("<template id=\"clip-row\">", html);
        Assert.Matches("<script type=\"module\" src=\"/js/history.js\\?v=[^\"]+\"></script>", html);
        // The modules history.js loads are versioned too, or Cloudflare could pair a new page with an old module.
        Assert.Matches("data-e2e=\"/js/e2e.js\\?v=[^\"]+\"", html);
        Assert.Matches("data-vault-ui=\"/js/vault-ui.js\\?v=[^\"]+\"", html);
    }

    [Fact]
    public async Task Delete_through_the_api_with_a_cookie_removes_the_clip()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        var clip = await factory.UploadTextAsync(api, "to be deleted");
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/");

        Assert.Equal(HttpStatusCode.BadRequest, (await web.DeleteAsync($"/api/clips/{clip.Id}")).StatusCode);
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/clips/{clip.Id}");
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add(ProtocolHeaders.Name, ProtocolHeaders.Version);
        Assert.Equal(HttpStatusCode.NoContent, (await web.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/clips/{clip.Id}")).StatusCode);
    }

    [Fact]
    public async Task Restore_via_cookie_requires_antiforgery()
    {
        using var factory = new PrivatePasteFactory();
        var clip = await factory.UploadTextAsync(await factory.LoginAsync(), "restore me");
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/");

        var without = await web.PostAsync($"/api/clips/{clip.Id}/restore", null);
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);

        var with = await web.SendAsync(ApiPost($"/api/clips/{clip.Id}/restore", token));
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
        var restored = await with.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options);
        Assert.Equal("Web", restored!.SourceDeviceName);
        Assert.Null(restored.SourceDeviceId);
        Assert.Equal("web", restored.SourceDevicePlatform);
    }

    [Fact]
    public async Task Bearer_api_calls_do_not_need_antiforgery()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        var clip = await factory.UploadTextAsync(api, "bearer ok");
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/clips/{clip.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task History_ships_every_platform_icon_for_the_rows()
    {
        using var factory = new PrivatePasteFactory();
        var html = await (await WebLoginAsync(factory)).GetStringAsync("/");
        var icons = Regex.Match(html, "<template id=\"platform-icons\">(.*?)</template>", RegexOptions.Singleline);
        Assert.True(icons.Success, "No platform icon template");
        foreach (var icon in PrivatePaste.Server.Pages.PlatformIcons.All)
            Assert.Contains(PrivatePaste.Server.Pages.PlatformIcons.Svg(icon.Key).ToString()!, icons.Groups[1].Value);
    }

    [Fact]
    public async Task People_page_shows_the_platform_icon_before_each_device_name()
    {
        using var factory = new PrivatePasteFactory();
        await factory.LoginAsync("Pixel", "android");
        await factory.LoginAsync("Air", "macos");
        var web = await WebLoginAsync(factory);

        var html = await web.GetStringAsync("/people");
        Assert.Matches("<span class=\"device-name\"><svg class=\"platform\" data-platform=\"android\"[^>]*>.*?</svg>Pixel</span>", html);
        Assert.Matches("<span class=\"device-name\"><svg class=\"platform\" data-platform=\"macos\"[^>]*aria-label=\"macOS\">.*?</svg>Air</span>", html);
    }

    [Fact]
    public async Task People_page_sign_out_device_works()
    {
        using var factory = new PrivatePasteFactory();
        var (device, deviceId, _) = await factory.LoginDeviceAsync("Laptop-revoke");
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/clips")).StatusCode);

        var web = await WebLoginAsync(factory);
        Assert.Contains("Laptop-revoke", await web.GetStringAsync("/people"));
        var token = await TokenFromAsync(web, "/people");
        var response = await PostFormAsync(web, "/people?handler=SignOutDevice", token, ("id", deviceId.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Sign_out_ends_the_web_session()
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/")).StatusCode);

        var token = await TokenFromAsync(web, "/people");
        var response = await PostFormAsync(web, "/people?handler=SignOut", token);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var after = await web.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Equal("/login", after.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task Api_logout_with_cookie_signs_the_session_out()
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/people");

        Assert.Equal(HttpStatusCode.NoContent, (await web.SendAsync(ApiPost("/api/auth/logout", token))).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await web.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Clip_upload_via_cookie_requires_antiforgery_without_buffering_the_body()
    {
        using var factory = new PrivatePasteFactory();
        var key = await factory.VaultKeyAsync();
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/");
        var image = new byte[200 * 1024];
        Random.Shared.NextBytes(image);
        var (meta, blob, thumb) = await PrivatePasteFactory.SealImageAsync(key, image);
        MultipartFormDataContent Form() => new()
        {
            { new StringContent(JsonSerializer.Serialize(meta, ProtocolJson.Options)), "meta" },
            { new ByteArrayContent(thumb), "thumb", "thumb" },
            { new ByteArrayContent(blob), "blob0", "blob0" },
        };

        var created = new System.Collections.Concurrent.ConcurrentBag<string>();
        using (var watcher = new FileSystemWatcher(Path.GetTempPath(), "ASPNETCORE_*") { EnableRaisingEvents = true })
        {
            watcher.Created += (_, e) => created.Add(e.FullPath);
            Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/clips", Form())).StatusCode);
            await Task.Delay(300);
        }
        Assert.Empty(created);

        var with = new HttpRequestMessage(HttpMethod.Post, "/api/clips") { Content = Form() };
        with.Headers.Add("RequestVerificationToken", token);
        Assert.Equal(HttpStatusCode.OK, (await web.SendAsync(with)).StatusCode);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/health")]
    [InlineData("/api/clips")]
    public async Task Responses_forbid_framing(string path)
    {
        using var factory = new PrivatePasteFactory();
        var response = await WebClient(factory).GetAsync(path);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }

    [Fact]
    public async Task Data_protection_keys_are_persisted_encrypted()
    {
        using var factory = new PrivatePasteFactory();
        await WebLoginAsync(factory);
        var keys = Directory.GetFiles(Path.Combine(factory.DataDir, "dp-keys"), "*.xml");
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            var xml = await File.ReadAllTextAsync(key);
            Assert.Contains("encryptedSecret", xml);
            Assert.DoesNotContain("<masterKey", xml);
        }
    }
}
