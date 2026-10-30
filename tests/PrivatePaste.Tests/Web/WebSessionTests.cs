using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrivatePaste.Server.Auth;

namespace PrivatePaste.Tests.Web;

public class WebSessionTests
{
    private static async Task<UserRecord> AddPersonAsync(PrivatePasteFactory factory, string name, string password)
    {
        var hash = (await factory.Services.GetRequiredService<PasswordVerifier>().HashAsync(password))!;
        return await factory.Services.GetRequiredService<UserRepository>().CreateAsync(name, hash, isOwner: false);
    }

    [Fact]
    public async Task WebLogin_WithName_ReachesHistory()
    {
        using var factory = new PrivatePasteFactory();
        var web = await factory.LoginWebAsync();
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task WebLogin_WrongPassword_ShowsWrongNameOrPassword()
    {
        using var factory = new PrivatePasteFactory();
        var client = factory.CreateWebClient();
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(client, "/login");
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "owner",
            ["Password"] = "nope",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Wrong name or password.", html);
        Assert.Contains("value=\"owner\"", html); // the name is kept, the password is not
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task LegacyWebOwnerCookie_StillSignedInAsOwner()
    {
        using var factory = new PrivatePasteFactory();
        factory.Clock.SetUtcNow(DateTimeOffset.UtcNow);

        // The principal web sessions carried before there were people: no person id, just "web-owner".
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "web-owner"),
                new Claim(DeviceClaims.Name, "Web"),
                new Claim(DeviceClaims.Platform, DeviceClaims.WebPlatform),
            ],
            AuthSchemes.Cookies);
        var now = DateTimeOffset.UtcNow;
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = now, ExpiresUtc = now.AddDays(1) },
            AuthSchemes.Cookies);
        var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AuthSchemes.Cookies);
        var cookies = new CookieContainer();
        cookies.Add(new Uri("http://localhost"),
            new Cookie(".AspNetCore.Cookies", cookieOptions.TicketDataFormat.Protect(ticket)));
        var web = factory.CreateDefaultClient(new Uri("http://localhost"), new CookieContainerHandler(cookies));

        var page = await web.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        // Renewed as the owner, so the next request carries the person's id.
        Assert.Contains(page.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Cookies="));

        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/");
        using var clear = new HttpRequestMessage(HttpMethod.Post, "/api/history/clear");
        clear.Headers.Add("RequestVerificationToken", token);
        Assert.Equal(HttpStatusCode.OK, (await web.SendAsync(clear)).StatusCode);
    }

    [Fact]
    public async Task CookieOfRemovedPerson_RedirectsToLogin()
    {
        using var factory = new PrivatePasteFactory();
        var ana = await AddPersonAsync(factory, "Ana", "ana-pass-1");
        var web = await factory.LoginWebAsync("Ana", "ana-pass-1");
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/")).StatusCode);

        await factory.Services.GetRequiredService<UserRepository>().MarkRemovedAsync(ana.Id);

        var response = await web.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task CookieOfPersonWithNewStamp_RedirectsToLogin()
    {
        using var factory = new PrivatePasteFactory();
        var ana = await AddPersonAsync(factory, "Ana", "ana-pass-1");
        var web = await factory.LoginWebAsync("Ana", "ana-pass-1");

        await factory.Services.GetRequiredService<UserRepository>()
            .SetPasswordAsync(ana.Id, ana.PasswordHash, newSecurityStamp: true);

        var response = await web.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task PasswordReset_WithoutNewStamp_KeepsSession()
    {
        using var factory = new PrivatePasteFactory();
        var web = await factory.LoginWebAsync();
        var users = factory.Services.GetRequiredService<UserRepository>();
        var owner = (await users.GetOwnerAsync())!;

        await users.SetPasswordAsync(owner.Id, owner.PasswordHash, newSecurityStamp: false);

        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/")).StatusCode);
    }
}
