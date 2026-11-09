using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Api;

public class AuthApiTests
{
    private static Task<HttpResponseMessage> LoginRaw(HttpClient c, string password, string name = "PC",
        string platform = "windows", string? username = PrivatePasteFactory.OwnerName) =>
        c.PostAsJsonAsync("/api/auth/login", new LoginRequest(password, name, platform) { Username = username },
            ProtocolJson.Options);

    private static async Task<string?> ProblemDetailAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("detail").GetString();
    }

    [Fact]
    public async Task Login_WithOwnerName_ReturnsToken()
    {
        using var factory = new PrivatePasteFactory();
        var response = await LoginRaw(factory.CreateClient(), PrivatePasteFactory.Password, username: "owner");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_NameIsCaseAndSpaceInsensitive()
    {
        using var factory = new PrivatePasteFactory();
        var response = await LoginRaw(factory.CreateClient(), PrivatePasteFactory.Password, username: "  OWNER ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Login_NoUsername_400_UpdateMessage(string? username)
    {
        using var factory = new PrivatePasteFactory();
        var response = await LoginRaw(factory.CreateClient(), PrivatePasteFactory.Password, username: username);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Update PrivatePaste to sign in.", await ProblemDetailAsync(response));
    }

    [Fact]
    public async Task Login_UnknownName_401_SameMessageAsWrongPassword()
    {
        using var factory = new PrivatePasteFactory();
        var client = factory.CreateClient();
        var unknown = await LoginRaw(client, PrivatePasteFactory.Password, username: "nobody");
        var wrong = await LoginRaw(client, "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("Wrong name or password.", await ProblemDetailAsync(unknown));
        Assert.Equal("Wrong name or password.", await ProblemDetailAsync(wrong));
    }

    [Fact]
    public async Task Login_DeviceBelongsToSignedInPerson()
    {
        using var factory = new PrivatePasteFactory();
        var (_, deviceId, _) = await factory.LoginDeviceAsync();
        var owner = (await factory.Services.GetRequiredService<UserRepository>().GetOwnerAsync())!;

        await using var conn = factory.Services.GetRequiredService<PrivatePasteDb>().Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM devices WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", deviceId.ToString());
        Assert.Equal(owner.Id.ToString(), await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Login_AsAnotherPerson_DeviceBelongsToThem()
    {
        using var factory = new PrivatePasteFactory();
        var users = factory.Services.GetRequiredService<UserRepository>();
        var hash = (await factory.Services.GetRequiredService<PasswordVerifier>().HashAsync("ana-pass-1"))!;
        var ana = await users.CreateAsync("Ana", hash, isOwner: false);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await LoginRaw(factory.CreateClient(), PrivatePasteFactory.Password, username: "ana")).StatusCode);
        var (client, deviceId, _) = await factory.LoginDeviceAsync(username: "ana", password: "ana-pass-1");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/clips")).StatusCode);

        await using var conn = factory.Services.GetRequiredService<PrivatePasteDb>().Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM devices WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", deviceId.ToString());
        Assert.Equal(ana.Id.ToString(), await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Token_of_removed_person_401()
    {
        using var factory = new PrivatePasteFactory();
        var users = factory.Services.GetRequiredService<UserRepository>();
        var hash = (await factory.Services.GetRequiredService<PasswordVerifier>().HashAsync("ana-pass-1"))!;
        var ana = await users.CreateAsync("Ana", hash, isOwner: false);
        var client = await factory.LoginAsync(username: "Ana", password: "ana-pass-1");

        await users.MarkRemovedAsync(ana.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/clips")).StatusCode);
    }

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
        client.DefaultRequestHeaders.Add(ProtocolHeaders.Name, ProtocolHeaders.Version);
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
        client.DefaultRequestHeaders.Add(ProtocolHeaders.Name, ProtocolHeaders.Version);
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

    private static async Task<DeviceDto> DeviceAsync(HttpClient client, string name) =>
        (await client.GetFromJsonAsync<DeviceDto[]>("/api/devices", ProtocolJson.Options))!.Single(d => d.Name == name);

    private static HttpRequestMessage WithAppVersion(string path, string version)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(ProtocolHeaders.AppVersion, version);
        return request;
    }

    [Fact]
    public async Task Login_records_app_version()
    {
        using var factory = new PrivatePasteFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ProtocolHeaders.AppVersion, "0.6.3");
        Assert.Equal(HttpStatusCode.OK, (await LoginRaw(client, PrivatePasteFactory.Password, name: "A")).StatusCode);

        Assert.Equal("0.6.3", (await DeviceAsync(await factory.LoginAsync("B"), "A")).AppVersion);
    }

    [Fact]
    public async Task Device_without_app_version_has_none()
    {
        using var factory = new PrivatePasteFactory();
        var client = await factory.LoginAsync("A");
        Assert.Null((await DeviceAsync(client, "A")).AppVersion);
    }

    [Fact]
    public async Task App_version_follows_an_upgrade_right_away()
    {
        using var factory = new PrivatePasteFactory();
        var client = await factory.LoginAsync("A");
        // Twice within the minute the last-seen time is kept for: a new version is still saved at once.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithAppVersion("/api/devices", "0.6.2"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithAppVersion("/api/devices", "0.7.0-rc1"))).StatusCode);
        Assert.Equal("0.7.0-rc1", (await DeviceAsync(client, "A")).AppVersion);

        // A request without the header keeps what was saved.
        Assert.Equal("0.7.0-rc1", (await DeviceAsync(client, "A")).AppVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.6.3 <script>")]
    [InlineData("123456789012345678901234567890123")]
    public async Task Malformed_app_version_is_ignored(string version)
    {
        using var factory = new PrivatePasteFactory();
        var client = await factory.LoginAsync("A");
        await client.SendAsync(WithAppVersion("/api/devices", "0.6.3"));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithAppVersion("/api/devices", version))).StatusCode);
        Assert.Equal("0.6.3", (await DeviceAsync(client, "A")).AppVersion);
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
