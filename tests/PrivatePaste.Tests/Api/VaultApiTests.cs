using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;
using PrivatePaste.Server.Storage;
using PrivatePaste.Tests.Realtime;

namespace PrivatePaste.Tests.Api;

public class VaultApiTests
{
    private static Task<HttpResponseMessage> PutAsync(HttpClient c, VaultWrapDto wrap) =>
        c.PutAsJsonAsync("/api/vault", wrap, ProtocolJson.Options);

    [Fact]
    public async Task NoVault_Returns404()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/vault")).StatusCode);
    }

    [Fact]
    public async Task Owner_CreatesThenRewraps()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        using var key = VaultKey.Create();

        var first = PrivatePasteFactory.RandomWrap(key.KeyId);
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(owner, first)).StatusCode);

        var second = PrivatePasteFactory.RandomWrap(key.KeyId);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(owner, second)).StatusCode);

        var got = await owner.GetFromJsonAsync<VaultWrapDto>("/api/vault", ProtocolJson.Options);
        Assert.Equal(second, got);
    }

    [Fact]
    public async Task RegularPerson_CanReadTheWrap()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        using var key = VaultKey.Create();
        var wrap = await PrivatePasteFactory.EnsureVaultAsync(owner, key);
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");

        Assert.Equal(wrap, await ana.GetFromJsonAsync<VaultWrapDto>("/api/vault", ProtocolJson.Options));
    }

    [Fact]
    public async Task WebSession_CanReadTheWrap()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        using var key = VaultKey.Create();
        var wrap = await PrivatePasteFactory.EnsureVaultAsync(owner, key);
        var web = await f.LoginWebAsync();

        Assert.Equal(wrap, await web.GetFromJsonAsync<VaultWrapDto>("/api/vault", ProtocolJson.Options));
    }

    [Fact]
    public async Task CreateTwiceWithOtherKey_Is409()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        using var key = VaultKey.Create();
        using var other = VaultKey.Create();
        var wrap = await PrivatePasteFactory.EnsureVaultAsync(owner, key);

        var response = await PutAsync(owner, PrivatePasteFactory.RandomWrap(other.KeyId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Encryption is already set up with a different key.", await response.Content.ReadAsStringAsync());
        Assert.Equal(wrap, await owner.GetFromJsonAsync<VaultWrapDto>("/api/vault", ProtocolJson.Options));
    }

    [Fact]
    public async Task NonOwner_CannotPutOrReset()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");
        using var key = VaultKey.Create();
        var wrap = PrivatePasteFactory.RandomWrap(key.KeyId);

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(ana, wrap)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ana.PostAsJsonAsync("/api/vault/reset", wrap, ProtocolJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ana.GetAsync("/api/vault")).StatusCode);
    }

    [Fact]
    public async Task Reset_WipesClipsAndBroadcasts()
    {
        using var f = new PrivatePasteFactory();
        var (owner, _, token) = await f.LoginDeviceAsync();
        using var key = VaultKey.Create();
        await PrivatePasteFactory.EnsureVaultAsync(owner, key);
        var clip = await f.UploadTextAsync(owner, key, "secret");
        (await owner.PostAsync($"/api/clips/{clip.Id}/pin", null)).EnsureSuccessStatusCode();
        // An image (blob and thumbnail files) and a deleted clip (a tombstone), so the wipe has all of it to remove.
        await f.UploadImageAsync(owner, key, [1, 2, 3, 4]);
        var blobDir = Path.Combine(f.DataDir, "blobs");
        Assert.NotEmpty(Directory.GetFiles(blobDir, "*.enc"));
        var doomedClip = await f.UploadTextAsync(owner, key, "deleted");
        (await owner.DeleteAsync($"/api/clips/{doomedClip.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(1, TombstoneCount(f));
        var settings = f.Services.GetRequiredService<SettingsRepository>();
        await settings.SetHistoryClearedAtAsync(DateTimeOffset.UtcNow);

        using var socket = await WebSocketTests.ConnectAsync(f, token);
        using var newKey = VaultKey.Create();
        var newWrap = PrivatePasteFactory.RandomWrap(newKey.KeyId);
        var reset = await owner.PostAsJsonAsync("/api/vault/reset", newWrap, ProtocolJson.Options);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var sync = await owner.GetFromJsonAsync<SyncResponse>("/api/sync?afterSeq=0", ProtocolJson.Options);
        Assert.Empty(sync!.Clips);
        Assert.Empty(sync.DeletedIds);
        Assert.Equal(0, TombstoneCount(f));
        Assert.Empty(Directory.GetFiles(blobDir, "*", SearchOption.AllDirectories));
        Assert.True(sync.LatestSeq >= clip.Seq, "the seq counter must not go back");
        Assert.Null(await settings.GetHistoryClearedAtAsync());
        Assert.Equal(newWrap, await owner.GetFromJsonAsync<VaultWrapDto>("/api/vault", ProtocolJson.Options));

        var (result, text) = await WebSocketTests.ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.Equal(WsTypes.VaultReset, JsonSerializer.Deserialize<WsMessage>(text, ProtocolJson.Options)!.Type);
    }

    private static long TombstoneCount(PrivatePasteFactory f)
    {
        using var conn = f.Services.GetRequiredService<PrivatePasteDb>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM tombstones";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task WeakKdfParams_Are400()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        using var key = VaultKey.Create();
        var good = PrivatePasteFactory.RandomWrap(key.KeyId);

        VaultWrapDto[] bad =
        [
            good with { Kdf = good.Kdf with { MemoryKiB = 1024 } },
            good with { Kdf = good.Kdf with { MemoryKiB = 524288 } },
            good with { Kdf = good.Kdf with { Iterations = 2 } },
            good with { Kdf = good.Kdf with { Iterations = 11 } },
            good with { Kdf = good.Kdf with { Parallelism = 0 } },
            good with { Kdf = good.Kdf with { Parallelism = 9 } },
            good with { Kdf = good.Kdf with { Alg = "pbkdf2" } },
            good with { Kdf = good.Kdf with { Salt = Convert.ToBase64String(new byte[15]) } },
            good with { Kdf = good.Kdf with { Salt = "not base64!" } },
            good with { Nonce = Convert.ToBase64String(new byte[16]) },
            good with { WrappedKey = Convert.ToBase64String(new byte[32]) },
            good with { KeyId = "ABCDEF0123456789" },
            good with { KeyId = "abc" },
            // Decodes to 12 bytes but isn't canonical: whitespace, and non-zero trailing bits in the last character.
            good with { Nonce = good.Nonce[..4] + " " + good.Nonce[4..] },
            good with { Kdf = good.Kdf with { Salt = "AAAAAAAAAAAAAAAAAAAAAB==" } },
        ];
        foreach (var wrap in bad)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(owner, wrap)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await owner.PostAsJsonAsync("/api/vault/reset", wrap, ProtocolJson.Options)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/vault")).StatusCode);
    }

    [Fact]
    public async Task Vault_WithoutProtocolHeader_Is426()
    {
        using var f = new PrivatePasteFactory();
        var legacy = await f.LoginAsync(legacy: true);
        Assert.Equal((HttpStatusCode)426, (await legacy.GetAsync("/api/vault")).StatusCode);
    }
}
