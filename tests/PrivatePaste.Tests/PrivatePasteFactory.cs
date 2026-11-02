using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Storage;

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
    private VaultKey? _vaultKey;

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

    /// <summary>
    /// Logs in as a new device and returns a client that sends its bearer token and the protocol header
    /// (<paramref name="legacy"/> omits the header, like a client from before protocol 2).
    /// </summary>
    public async Task<HttpClient> LoginAsync(string deviceName = "PC-test", string platform = "windows",
        string username = OwnerName, string password = Password, bool legacy = false) =>
        (await LoginDeviceAsync(deviceName, platform, username, password, legacy)).Client;

    /// <summary>Logs in as a new device and returns its client, id and raw bearer token.</summary>
    public async Task<(HttpClient Client, Guid DeviceId, string Token)> LoginDeviceAsync(string deviceName = "PC-test",
        string platform = "windows", string username = OwnerName, string password = Password, bool legacy = false)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(password, deviceName, platform) { Username = username }, ProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(ProtocolJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        if (!legacy)
            client.DefaultRequestHeaders.Add(ProtocolHeaders.Name, ProtocolHeaders.Version);
        return (client, login.DeviceId, login.Token);
    }

    /// <summary>
    /// Stores a syntactically valid vault wrap for <paramref name="key"/> as the owner. The server never opens the
    /// wrap, so its bytes are random; tests that need a real wrap build one with <c>PassphraseWrap</c>.
    /// </summary>
    public static async Task<VaultWrapDto> EnsureVaultAsync(HttpClient ownerClient, VaultKey key)
    {
        var wrap = RandomWrap(key.KeyId);
        var response = await ownerClient.PutAsJsonAsync("/api/vault", wrap, ProtocolJson.Options);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, response.StatusCode.ToString());
        return wrap;
    }

    /// <summary>Sets up a vault with a new key as the owner and returns the key, for uploading clips.</summary>
    public static async Task<VaultKey> SetUpVaultAsync(HttpClient ownerClient)
    {
        var key = VaultKey.Create();
        await EnsureVaultAsync(ownerClient, key);
        return key;
    }

    /// <summary>
    /// The vault key shared by this factory's tests. The first call stores a vault for it straight in the database, so
    /// any person's device can upload under it without the owner setting it up first.
    /// </summary>
    public async Task<VaultKey> VaultKeyAsync()
    {
        if (_vaultKey is null)
        {
            var key = VaultKey.Create();
            Assert.True(await Services.GetRequiredService<VaultRepository>().CreateAsync(RandomWrap(key.KeyId)),
                "A vault already exists");
            _vaultKey = key;
        }

        return _vaultKey;
    }

    /// <summary>Uploads <paramref name="text"/> sealed under <see cref="VaultKeyAsync"/>'s key.</summary>
    public async Task<ClipDto> UploadTextAsync(HttpClient client, string text, DateTimeOffset? copiedAt = null,
        bool backfill = false) =>
        await UploadTextAsync(client, await VaultKeyAsync(), text, copiedAt, backfill);

    /// <summary>Seals <paramref name="text"/> as a client would and uploads it; fails the test unless it is stored.</summary>
    public async Task<ClipDto> UploadTextAsync(HttpClient client, VaultKey key, string text, DateTimeOffset? copiedAt = null,
        bool backfill = false) =>
        await ReadClipAsync(await PostClipAsync(client, TextMeta(key, text, copiedAt, backfill)));

    /// <summary>
    /// Seals <paramref name="image"/> (any bytes: the server never opens it) and a thumbnail as a client would and
    /// uploads them; fails the test unless it is stored. Returns the clip and the sealed blob and thumbnail sent.
    /// </summary>
    public async Task<(ClipDto Clip, byte[] Blob, byte[] Thumb)> UploadImageAsync(HttpClient client, VaultKey key,
        byte[] image, DateTimeOffset? copiedAt = null, bool backfill = false)
    {
        var (meta, blob, thumb) = await SealImageAsync(key, image, copiedAt, backfill);
        return (await ReadClipAsync(await PostClipAsync(client, meta, thumb, blob)), blob, thumb);
    }

    /// <summary>The upload metadata a client sends for <paramref name="text"/>.</summary>
    public static ClipUploadMeta TextMeta(VaultKey key, string text, DateTimeOffset? copiedAt = null, bool backfill = false)
    {
        var normalized = ContentHash.NormalizeText(text);
        var kind = ContentClassifier.ClassifyText(normalized) == ClipKind.Link ? "link" : "text";
        return new ClipUploadMeta(kind, key.KeyId, KeyedHash.For(key, ContentHash.ForText(normalized)),
            ClipPayload.Seal(key, new ClipPayload { Text = normalized }), Encoding.UTF8.GetByteCount(normalized),
            copiedAt, backfill);
    }

    /// <summary>The metadata, sealed blob and sealed thumbnail a client sends for an image.</summary>
    public static async Task<(ClipUploadMeta Meta, byte[] Blob, byte[] Thumb)> SealImageAsync(VaultKey key, byte[] image,
        DateTimeOffset? copiedAt = null, bool backfill = false)
    {
        using var sealedBlob = new MemoryStream();
        await BlobCipher.EncryptAsync(key, new MemoryStream(image), sealedBlob, CancellationToken.None);
        var thumb = SealedBox.Seal(key, "thumb", image.AsSpan(0, Math.Min(image.Length, 64)));
        var meta = new ClipUploadMeta("image", key.KeyId,
            KeyedHash.For(key, Convert.ToHexStringLower(SHA256.HashData(image))),
            ClipPayload.Seal(key, new ClipPayload { Width = 4, Height = 4 }), image.Length, copiedAt, backfill);
        return (meta, sealedBlob.ToArray(), thumb);
    }

    /// <summary>
    /// The metadata and sealed blobs a client sends for a file clip. <paramref name="files"/> are the plaintexts; the
    /// names only go into the sealed payload.
    /// </summary>
    public static async Task<(ClipUploadMeta Meta, byte[][] Blobs)> SealFilesAsync(VaultKey key, params byte[][] files)
    {
        var blobs = new List<byte[]>();
        var infos = new List<ClipFileInfo>();
        var hashes = new List<(string, long, string)>();
        for (var i = 0; i < files.Length; i++)
        {
            using var sealedBlob = new MemoryStream();
            await BlobCipher.EncryptAsync(key, new MemoryStream(files[i]), sealedBlob, CancellationToken.None);
            blobs.Add(sealedBlob.ToArray());
            infos.Add(new ClipFileInfo($"file{i}.bin", files[i].Length, "application/octet-stream"));
            hashes.Add(($"file{i}.bin", files[i].Length, Convert.ToHexStringLower(SHA256.HashData(files[i]))));
        }

        var meta = new ClipUploadMeta("file", key.KeyId, KeyedHash.For(key, ContentHash.ForFiles(hashes)),
            ClipPayload.Seal(key, new ClipPayload { Files = infos }), files.Sum(f => (long)f.Length), null, false);
        return (meta, blobs.ToArray());
    }

    /// <summary>Posts an upload as a client would: the metadata as JSON, then the optional thumbnail and blobs.</summary>
    public static Task<HttpResponseMessage> PostClipAsync(HttpClient client, ClipUploadMeta meta, byte[]? thumb = null,
        params byte[][] blobs)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(meta, ProtocolJson.Options), Encoding.UTF8, "application/json"), "meta" },
        };
        if (thumb is not null)
            form.Add(new ByteArrayContent(thumb), "thumb", "thumb");
        for (var n = 0; n < blobs.Length; n++)
            form.Add(new ByteArrayContent(blobs[n]), $"blob{n}", $"blob{n}");
        return client.PostAsync("/api/clips", form);
    }

    private static async Task<ClipDto> ReadClipAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    /// <summary>A valid-looking wrap with random salt, nonce and wrapped key.</summary>
    public static VaultWrapDto RandomWrap(string keyId) => new(keyId,
        new VaultKdfDto("argon2id", 65536, 3, 1, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)),
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));

    /// <summary>
    /// Adds a regular person, as the owner would, and returns their id; sign in as them with
    /// <c>LoginAsync(username: name, password: ...)</c>.
    /// </summary>
    public async Task<Guid> AddPersonAsync(string name, string password = "person-pass-1")
    {
        var result = await Services.GetRequiredService<PeopleService>().AddAsync(name, password);
        Assert.True(result.Ok, result.Error);
        return (await Services.GetRequiredService<UserRepository>().FindCurrentByNameAsync(name))!.Id;
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
        if (disposing)
            _vaultKey?.Dispose();
        if (disposing && _ownsDataDir)
        {
            try { Directory.Delete(_dataDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
