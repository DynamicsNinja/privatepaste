using System.Net;
using System.Net.Http.Json;
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

    private static async Task<HttpClient> WebLoginAsync(PrivatePasteFactory factory)
    {
        var client = WebClient(factory);
        var token = await TokenFromAsync(client, "/login");
        var response = await PostFormAsync(client, "/login", token, ("Password", PrivatePasteFactory.Password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        return client;
    }

    private static async Task<ClipDto> UploadTextAsync(HttpClient api, string text)
    {
        var response = await api.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, false), ProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

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
        var response = await PostFormAsync(client, "/login", token, ("Password", "nope"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Wrong password.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_is_rate_limited()
    {
        using var factory = new PrivatePasteFactory();
        var client = WebClient(factory);
        var token = await TokenFromAsync(client, "/login");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostFormAsync(client, "/login", token, ("Password", "nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostFormAsync(client, "/login", token, ("Password", "nope"))).StatusCode);
        // Viewing the page is never limited.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/login")).StatusCode);
    }

    [Fact]
    public async Task Login_cookie_is_httponly_and_samesite_strict()
    {
        using var factory = new PrivatePasteFactory();
        var client = WebClient(factory);
        var token = await TokenFromAsync(client, "/login");
        var response = await PostFormAsync(client, "/login", token, ("Password", PrivatePasteFactory.Password));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Cookies="));
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_then_index_lists_clips()
    {
        using var factory = new PrivatePasteFactory();
        await UploadTextAsync(await factory.LoginAsync(), "hello web");
        var web = await WebLoginAsync(factory);
        var response = await web.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hello web", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Index_html_encodes_clip_text()
    {
        using var factory = new PrivatePasteFactory();
        await UploadTextAsync(await factory.LoginAsync(), "<script>alert(1)</script>");
        var body = await (await (await WebLoginAsync(factory)).GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;", body);
        Assert.DoesNotContain("<script>alert(1)", body);
    }

    [Fact]
    public async Task Index_renders_links_as_safe_anchors()
    {
        using var factory = new PrivatePasteFactory();
        await UploadTextAsync(await factory.LoginAsync(), "https://example.com/a");
        var body = await (await (await WebLoginAsync(factory)).GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("<a href=\"https://example.com/a\" rel=\"noopener noreferrer\" target=\"_blank\">", body);
    }

    [Fact]
    public async Task Index_search_filters()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        await UploadTextAsync(api, "docker compose up");
        await UploadTextAsync(api, "grocery list");
        var body = await (await (await WebLoginAsync(factory)).GetAsync("/?q=compose")).Content.ReadAsStringAsync();
        Assert.Contains("docker compose up", body);
        Assert.DoesNotContain("grocery list", body);
    }

    [Fact]
    public async Task Index_paginates_with_load_more()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        for (var i = 0; i < 51; i++)
            await UploadTextAsync(api, $"clip-number-{i:D3}");
        var web = await WebLoginAsync(factory);
        var first = await web.GetStringAsync("/");
        Assert.Contains("Load more", first);
        Assert.Equal(50, Regex.Matches(first, "clip-number-").Count);
        var cursor = Regex.Match(first, "href=\"/\\?cursor=([^\"]+)\"").Groups[1].Value;
        var second = await web.GetStringAsync("/?cursor=" + System.Net.WebUtility.HtmlDecode(cursor));
        Assert.Single(Regex.Matches(second, "clip-number-"));
        Assert.DoesNotContain("Load more", second);
    }

    [Fact]
    public async Task Delete_from_web_removes_clip()
    {
        using var factory = new PrivatePasteFactory();
        var api = await factory.LoginAsync();
        var clip = await UploadTextAsync(api, "to be deleted");
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/");
        var response = await PostFormAsync(web, "/?handler=Delete", token, ("id", clip.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/clips/{clip.Id}")).StatusCode);
    }

    [Fact]
    public async Task Restore_via_cookie_requires_antiforgery()
    {
        using var factory = new PrivatePasteFactory();
        var clip = await UploadTextAsync(await factory.LoginAsync(), "restore me");
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
        var clip = await UploadTextAsync(api, "bearer ok");
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/clips/{clip.Id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Devices_page_revoke_works()
    {
        using var factory = new PrivatePasteFactory();
        var (device, deviceId, _) = await factory.LoginDeviceAsync("Laptop-revoke");
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/clips")).StatusCode);

        var web = await WebLoginAsync(factory);
        Assert.Contains("Laptop-revoke", await web.GetStringAsync("/devices"));
        var token = await TokenFromAsync(web, "/devices");
        var response = await PostFormAsync(web, "/devices?handler=Revoke", token, ("id", deviceId.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Sign_out_ends_the_web_session()
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/")).StatusCode);

        var token = await TokenFromAsync(web, "/devices");
        var response = await PostFormAsync(web, "/devices?handler=SignOut", token);
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
        var token = await TokenFromAsync(web, "/devices");

        Assert.Equal(HttpStatusCode.NoContent, (await web.SendAsync(ApiPost("/api/auth/logout", token))).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await web.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Image_upload_via_cookie_requires_antiforgery_without_buffering_the_body()
    {
        using var factory = new PrivatePasteFactory();
        var web = await WebLoginAsync(factory);
        var token = await TokenFromAsync(web, "/");
        var bytes = Images.TestImages.MakePng(200, 200).ToArray();
        Assert.True(bytes.Length > 100 * 1024);
        MultipartFormDataContent Form() => new() { { new ByteArrayContent(bytes), "file", "x.png" } };

        var created = new System.Collections.Concurrent.ConcurrentBag<string>();
        using (var watcher = new FileSystemWatcher(Path.GetTempPath(), "ASPNETCORE_*") { EnableRaisingEvents = true })
        {
            watcher.Created += (_, e) => created.Add(e.FullPath);
            Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/clips/image", Form())).StatusCode);
            await Task.Delay(300);
        }
        Assert.Empty(created);

        var with = new HttpRequestMessage(HttpMethod.Post, "/api/clips/image") { Content = Form() };
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
