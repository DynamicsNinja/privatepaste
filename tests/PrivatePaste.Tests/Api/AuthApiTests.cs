using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Api;

public class AuthApiTests
{
    private static Task<HttpResponseMessage> LoginRaw(HttpClient c, string password, string name = "PC", string platform = "windows") =>
        c.PostAsJsonAsync("/api/auth/login", new LoginRequest(password, name, platform), ProtocolJson.Options);

    [Fact]
    public async Task Login_with_correct_password_returns_token()
    {
        using var factory = new PrivatePasteFactory();
        var response = await LoginRaw(factory.CreateClient(), PrivatePasteFactory.Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(ProtocolJson.Options))!;
        Assert.False(string.IsNullOrEmpty(login.Token));
        Assert.NotEqual(Guid.Empty, login.DeviceId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_401()
    {
        using var factory = new PrivatePasteFactory();
        var response = await LoginRaw(factory.CreateClient(), "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_rate_limited_after_5_attempts()
    {
        using var factory = new PrivatePasteFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRaw(client, "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginRaw(client, PrivatePasteFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task Api_without_token_401()
    {
        using var factory = new PrivatePasteFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Garbage_token_401()
    {
        using var factory = new PrivatePasteFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "garbage");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Device_list_marks_current()
    {
        using var factory = new PrivatePasteFactory();
        await factory.LoginAsync("A");
        var b = await factory.LoginAsync("B");
        var devices = (await b.GetFromJsonAsync<DeviceDto[]>("/api/devices", ProtocolJson.Options))!;
        Assert.Equal(2, devices.Length);
        Assert.True(devices.Single(d => d.Name == "B").IsCurrent);
        Assert.False(devices.Single(d => d.Name == "A").IsCurrent);
    }

    [Fact]
    public async Task Logout_revokes_own_token()
    {
        using var factory = new PrivatePasteFactory();
        var client = await factory.LoginAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Revoked_device_gets_401()
    {
        using var factory = new PrivatePasteFactory();
        var a = await factory.LoginAsync("A");
        var b = await factory.LoginAsync("B");
        var devices = (await a.GetFromJsonAsync<DeviceDto[]>("/api/devices", ProtocolJson.Options))!;
        var bId = devices.Single(d => d.Name == "B").Id;

        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/devices/{bId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/clips")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.DeleteAsync($"/api/devices/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Device_name_too_long_400()
    {
        using var factory = new PrivatePasteFactory();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await LoginRaw(client, PrivatePasteFactory.Password, new string('x', 51))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await LoginRaw(client, PrivatePasteFactory.Password, "")).StatusCode);
    }
}
