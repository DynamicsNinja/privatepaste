using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests;

public sealed class PrivatePasteFactory : WebApplicationFactory<Program>
{
    public const string Password = "hunter2-test";

    public const string OwnerName = "owner";
    public const string DbKey = "test-key-0123456789abcdef";

    private static readonly Regex TokenInput = new("<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ValueAttr = new("value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly string _dataDir;
    private readonly bool _ownsDataDir;
    private readonly IReadOnlyDictionary<string, string> _settings;

    /// <param name="dataDir">An existing data directory to reuse (a restart); it is left in place on dispose.</param>
    public PrivatePasteFactory(IReadOnlyDictionary<string, string>? settings = null, string? dataDir = null)
    {
        _settings = settings ?? new Dictionary<string, string>();
        _ownsDataDir = dataDir is null;
        _dataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "privatepaste-tests-" + Guid.NewGuid().ToString("N"));
    }

    public string DataDir => _dataDir;

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDir);
        builder.UseSetting("PP_DB_KEY", DbKey);
        builder.UseSetting("PP_PASSWORD", Password);
        builder.UseSetting("PP_DATA_DIR", _dataDir);
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>Logs in as a new device and returns a client that sends its bearer token.</summary>
    public async Task<HttpClient> LoginAsync(string deviceName = "PC-test", string platform = "windows",
        string username = OwnerName, string password = Password) =>
        (await LoginDeviceAsync(deviceName, platform, username, password)).Client;

    /// <summary>Logs in as a new device and returns its client, id and raw bearer token.</summary>
    public async Task<(HttpClient Client, Guid DeviceId, string Token)> LoginDeviceAsync(string deviceName = "PC-test",
        string platform = "windows", string username = OwnerName, string password = Password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(password, deviceName, platform) { Username = username }, ProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(ProtocolJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return (client, login.DeviceId, login.Token);
    }

    /// <summary>
    /// Signs in on the web login page and returns a client that keeps the session and antiforgery cookies and does
    /// not follow redirects.
    /// </summary>
    public async Task<HttpClient> LoginWebAsync(string username = OwnerName, string password = Password)
    {
        var client = CreateWebClient();
        var token = await AntiforgeryTokenAsync(client, "/login");
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = username,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        return client;
    }

    /// <summary>A client that keeps cookies and does not follow redirects, like a browser on the web UI.</summary>
    public HttpClient CreateWebClient()
    {
        // The test client's cookie jar uses the real clock; the server's fake clock would issue already-expired cookies.
        Clock.SetUtcNow(DateTimeOffset.UtcNow);
        return CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    /// <summary>The antiforgery token in the form on <paramref name="path"/>.</summary>
    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var input = TokenInput.Match(html);
        Assert.True(input.Success, $"No antiforgery token on {path}");
        return ValueAttr.Match(input.Value).Groups[1].Value;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsDataDir)
        {
            try { Directory.Delete(_dataDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
