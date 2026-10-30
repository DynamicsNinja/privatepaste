using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Storage;

public class OwnerBootstrapTests
{
    private static string NewDataDir() =>
        Path.Combine(Path.GetTempPath(), "privatepaste-tests-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDataDir(string dataDir)
    {
        try { Directory.Delete(dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task FirstStart_CreatesOwnerNamedOwner_WithPpPassword()
    {
        using var factory = new PrivatePasteFactory();
        var users = factory.Services.GetRequiredService<UserRepository>();
        var verifier = factory.Services.GetRequiredService<PasswordVerifier>();

        var owner = await users.GetOwnerAsync();
        Assert.NotNull(owner);
        Assert.Equal("owner", owner.Name);
        Assert.True(owner.IsOwner);
        Assert.Equal(PasswordCheck.Valid, await verifier.VerifyAsync(PrivatePasteFactory.Password, owner.PasswordHash));
        Assert.Equal(1, await users.CountCurrentAsync());
    }

    [Fact]
    public async Task Upgrade_ExistingDevicesAndClips_BelongToOwner_TokenStillWorks()
    {
        // A database from before people existed: a device and a clip, nobody in the users table.
        var dataDir = NewDataDir();
        const string token = "old-device-token";
        var deviceId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        try
        {
            var db = new PrivatePasteDb(new ServerOptions(PrivatePasteFactory.DbKey, "unused", dataDir, 1024, 1024));
            db.Initialize();
            await using (var conn = db.Open())
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO devices(id, name, platform, token_hash, created_at)
                    VALUES(@device, 'Old PC', 'windows', @hash, '2025-01-01T00:00:00.0000000Z');
                    INSERT INTO clips(id, seq, kind, text, hash, size_bytes, source_device_id, source_device_name,
                                      created_at, last_copied_at)
                    VALUES(@clip, 1, 'text', 'old', 'h1', 3, @device, 'Old PC',
                           '2025-01-01T00:00:00.0000000Z', '2025-01-01T00:00:00.0000000Z');
                    """;
                cmd.Parameters.AddWithValue("@device", deviceId.ToString());
                cmd.Parameters.AddWithValue("@clip", clipId.ToString());
                cmd.Parameters.AddWithValue("@hash",
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
                await cmd.ExecuteNonQueryAsync();
            }

            using var factory = new PrivatePasteFactory(dataDir: dataDir);
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/clips")).StatusCode);

            var owner = (await factory.Services.GetRequiredService<UserRepository>().GetOwnerAsync())!;
            await using var check = factory.Services.GetRequiredService<PrivatePasteDb>().Open();
            await using var q = check.CreateCommand();
            q.CommandText = "SELECT (SELECT user_id FROM devices WHERE id = @device) || '|' || " +
                            "(SELECT source_user_id FROM clips WHERE id = @clip)";
            q.Parameters.AddWithValue("@device", deviceId.ToString());
            q.Parameters.AddWithValue("@clip", clipId.ToString());
            Assert.Equal($"{owner.Id}|{owner.Id}", await q.ExecuteScalarAsync());
        }
        finally
        {
            DeleteDataDir(dataDir);
        }
    }

    [Fact]
    public async Task Restart_SamePpPassword_KeepsPasswordSetOnWeb()
    {
        using var factory = new PrivatePasteFactory();
        var users = factory.Services.GetRequiredService<UserRepository>();
        var verifier = factory.Services.GetRequiredService<PasswordVerifier>();
        var owner = (await users.GetOwnerAsync())!;

        await users.SetPasswordAsync(owner.Id, (await verifier.HashAsync("new-web-pass"))!, newSecurityStamp: true);
        await factory.Services.GetRequiredService<OwnerBootstrap>().EnsureOwnerAsync();

        var after = (await users.GetOwnerAsync())!;
        Assert.Equal(PasswordCheck.Valid, await verifier.VerifyAsync("new-web-pass", after.PasswordHash));
        Assert.Equal(PasswordCheck.Invalid, await verifier.VerifyAsync(PrivatePasteFactory.Password, after.PasswordHash));
    }

    [Fact]
    public async Task Restart_ChangedPpPassword_ResetsOwnerPassword_KeepsStamp()
    {
        var dataDir = NewDataDir();
        try
        {
            string stamp;
            using (var first = new PrivatePasteFactory(dataDir: dataDir))
                stamp = (await first.Services.GetRequiredService<UserRepository>().GetOwnerAsync())!.SecurityStamp;

            using var second = new PrivatePasteFactory(
                new Dictionary<string, string> { ["PP_PASSWORD"] = "recovered-pass" }, dataDir);
            var owner = (await second.Services.GetRequiredService<UserRepository>().GetOwnerAsync())!;
            var verifier = second.Services.GetRequiredService<PasswordVerifier>();
            Assert.Equal(PasswordCheck.Valid, await verifier.VerifyAsync("recovered-pass", owner.PasswordHash));
            Assert.Equal(PasswordCheck.Invalid, await verifier.VerifyAsync(PrivatePasteFactory.Password, owner.PasswordHash));
            Assert.Equal(stamp, owner.SecurityStamp);
        }
        finally
        {
            DeleteDataDir(dataDir);
        }
    }

    [Fact]
    public async Task CreateAsync_NameTakenIgnoringCase_Throws()
    {
        using var factory = new PrivatePasteFactory();
        var users = factory.Services.GetRequiredService<UserRepository>();
        var hash = (await factory.Services.GetRequiredService<PasswordVerifier>().HashAsync("pw-123456"))!;

        await users.CreateAsync("Ana", hash, isOwner: false);
        await Assert.ThrowsAsync<NameTakenException>(() => users.CreateAsync(" ANA ", hash, isOwner: false));
        await Assert.ThrowsAsync<NameTakenException>(() => users.CreateAsync("Owner", hash, isOwner: false));
    }

    [Fact]
    public async Task Names_AreFreedByRemoval_AndRenamesCheckTheIndex()
    {
        using var factory = new PrivatePasteFactory();
        var users = factory.Services.GetRequiredService<UserRepository>();
        var hash = (await factory.Services.GetRequiredService<PasswordVerifier>().HashAsync("pw-123456"))!;

        var ana = await users.CreateAsync("Ana", hash, isOwner: false);
        var bo = await users.CreateAsync("Bo", hash, isOwner: false);
        await Assert.ThrowsAsync<NameTakenException>(() => users.RenameAsync(bo.Id, "ana"));

        await users.MarkRemovedAsync(ana.Id);
        Assert.Null(await users.FindCurrentByNameAsync("ana"));
        var removed = (await users.GetAsync(ana.Id))!;
        Assert.NotNull(removed.RemovedAt);
        Assert.NotEqual(ana.SecurityStamp, removed.SecurityStamp);

        await users.RenameAsync(bo.Id, "Ana");
        Assert.Equal(bo.Id, (await users.FindCurrentByNameAsync(" ANA "))!.Id);
        Assert.Equal(["owner", "Ana"], (await users.ListCurrentAsync()).Select(u => u.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateName_Blank_IsRejected(string? name) =>
        Assert.Equal("Name must be 1-32 characters.", UserRepository.ValidateName(name));

    [Fact]
    public void ValidateName_LengthIsCountedAfterTrimming()
    {
        Assert.Null(UserRepository.ValidateName("  " + new string('a', 32) + "  "));
        Assert.Equal("Name must be 1-32 characters.", UserRepository.ValidateName(new string('a', 33)));
    }
}
