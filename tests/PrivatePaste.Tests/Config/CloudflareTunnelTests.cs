using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Config;

public class CloudflareTunnelTests
{
    private static readonly Dictionary<string, string> Trusted = new() { ["PP_TRUST_CLOUDFLARE"] = "true" };

    private static async Task<HttpStatusCode> LoginFromAsync(HttpClient client, string ip, string password = "nope")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(password, "PC", "windows") { Username = PrivatePasteFactory.OwnerName }, options: ProtocolJson.Options),
        };
        request.Headers.Add("CF-Connecting-IP", ip);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task Login_limit_is_per_cloudflare_client_ip_when_trusted()
    {
        using var factory = new PrivatePasteFactory(Trusted);
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginFromAsync(client, "203.0.113.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFromAsync(client, "203.0.113.7"));
        // A different visitor behind the same tunnel is not locked out.
        Assert.Equal(HttpStatusCode.OK, await LoginFromAsync(client, "198.51.100.9", PrivatePasteFactory.Password));
    }

    [Fact]
    public async Task Cloudflare_headers_are_ignored_when_not_trusted()
    {
        using var factory = new PrivatePasteFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginFromAsync(client, "203.0.113.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFromAsync(client, "198.51.100.9", PrivatePasteFactory.Password));
    }

    [Fact]
    public async Task Garbage_cloudflare_ip_falls_back_to_connection_ip()
    {
        using var factory = new PrivatePasteFactory(Trusted);
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginFromAsync(client, "not-an-ip-" + i));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFromAsync(client, "also-not-an-ip"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Session_cookie_is_secure_only_for_https_cloudflare_visitors_when_trusted(bool trusted, bool expectSecure)
    {
        using var factory = new PrivatePasteFactory(trusted ? Trusted : null);
        factory.Clock.SetUtcNow(DateTimeOffset.UtcNow);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.Add("Cf-Visitor", "{\"scheme\":\"https\"}");

        var html = await client.GetStringAsync("/login");
        var input = Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>").Value;
        var token = Regex.Match(input, "value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = PrivatePasteFactory.OwnerName,
            ["Password"] = PrivatePasteFactory.Password,
            ["__RequestVerificationToken"] = token,
        }));

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Cookies="));
        Assert.Equal(expectSecure, cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }
}
