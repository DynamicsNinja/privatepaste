using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "privatepaste-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IReadOnlyDictionary<string, string> _settings;

    public PrivatePasteFactory(IReadOnlyDictionary<string, string>? settings = null) =>
        _settings = settings ?? new Dictionary<string, string>();

    public string DataDir => _dataDir;

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDir);
        builder.UseSetting("PP_DB_KEY", "test-key-0123456789abcdef");
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
    public async Task<HttpClient> LoginAsync(string deviceName = "PC-test")
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(Password, deviceName, "windows"), ProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(ProtocolJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_dataDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
