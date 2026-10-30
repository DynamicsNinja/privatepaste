using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        SqliteConnection.ClearAllPools();
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
    public async Task Restart_ChangedPpPassword_ResetsOwnerPassword_NewStamp()
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
            Assert.NotEqual(stamp, owner.SecurityStamp);
        }
        finally
        {
            DeleteDataDir(dataDir);
        }
    }

    /// <summary>The bootstrap as a restart with <paramref name="password"/> as PP_PASSWORD would run it.</summary>
    private static OwnerBootstrap BootstrapWith(IServiceProvider services, string password) =>
        new(services.GetRequiredService<UserRepository>(), services.GetRequiredService<PasswordVerifier>(),
            services.GetRequiredService<SettingsRepository>(), services.GetRequiredService<PrivatePasteDb>(),
            services.GetRequiredService<ServerOptions>() with { Password = password },
            services.GetRequiredService<ILogger<OwnerBootstrap>>());

    [Fact]
    public async Task Restart_ChangedPpPassword_EndsOwnerWebSessions_KeepsDevices()
    {
        using var factory = new PrivatePasteFactory();
        var web = await factory.LoginWebAsync();
        var device = await factory.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/")).StatusCode);

        await BootstrapWith(factory.Services, "recovered-pass").EnsureOwnerAsync();

        var response = await web.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Restart_SamePpPassword_GivesDevicesWithoutOwnerToOwner()
    {
        // A device the previous release created after a rollback: it has no person.
        using var factory = new PrivatePasteFactory();
        const string token = "rollback-device-token";
        await using (var conn = factory.Services.GetRequiredService<PrivatePasteDb>().Open())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO devices(id, name, platform, token_hash, created_at)
                VALUES(@device, 'Rollback PC', 'windows', @hash, '2026-01-01T00:00:00.0000000Z')
                """;
            cmd.Parameters.AddWithValue("@device", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
            await cmd.ExecuteNonQueryAsync();
        }

        await BootstrapWith(factory.Services, PrivatePasteFactory.Password).EnsureOwnerAsync();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Restart_ChangedPpPassword_TurnsOffLegacyWebSessions()
    {
        var dataDir = NewDataDir();
        try
        {
            using (var first = new PrivatePasteFactory(dataDir: dataDir))
                Assert.True(await first.Services.GetRequiredService<SettingsRepository>()
                    .GetLegacyWebSessionsAllowedAsync());

            using (var same = new PrivatePasteFactory(dataDir: dataDir))
                Assert.True(await same.Services.GetRequiredService<SettingsRepository>()
                    .GetLegacyWebSessionsAllowedAsync());

            using var changed = new PrivatePasteFactory(
                new Dictionary<string, string> { ["PP_PASSWORD"] = "recovered-pass" }, dataDir);
            Assert.False(await changed.Services.GetRequiredService<SettingsRepository>()
                .GetLegacyWebSessionsAllowedAsync());
        }
        finally
        {
            DeleteDataDir(dataDir);
        }
    }

    [Fact]
    public async Task Restart_WhileHashGateIsFull_DoesNotResetOwnerPassword()
    {
        using var factory = new PrivatePasteFactory();
        var services = factory.Services;
        var users = services.GetRequiredService<UserRepository>();
        var owner = (await users.GetOwnerAsync())!;
        await users.SetPasswordAsync(owner.Id, (await new PasswordVerifier().HashAsync("new-web-pass"))!,
            newSecurityStamp: false);

        // A verifier whose gate is full: every check answers Busy until the slots are released.
        var gate = new ConcurrencyGate(PasswordVerifier.MaxConcurrentHashes, PasswordVerifier.MaxQueuedHashes);
        var held = Enumerable.Range(0, PasswordVerifier.MaxConcurrentHashes + PasswordVerifier.MaxQueuedHashes)
            .Select(_ => gate.TryEnterAsync()).ToList();
        var verifier = new PasswordVerifier(gate);
        Assert.Equal(PasswordCheck.Busy, await verifier.VerifyAsync(PrivatePasteFactory.Password, owner.PasswordHash));

        var bootstrap = new OwnerBootstrap(users, verifier, services.GetRequiredService<SettingsRepository>(),
            services.GetRequiredService<PrivatePasteDb>(), services.GetRequiredService<ServerOptions>(),
            services.GetRequiredService<ILogger<OwnerBootstrap>>());
        var ensure = bootstrap.EnsureOwnerAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(ensure.IsCompleted);
        foreach (var lease in held)
            (await lease)!.Dispose();
        await ensure;

        var after = (await users.GetOwnerAsync())!;
        Assert.Equal(PasswordCheck.Valid, await verifier.VerifyAsync("new-web-pass", after.PasswordHash));
    }

    [Fact]
    public async Task Upgrade_FromOldSchema_AddsColumns_AndGivesRowsToOwner()
    {
        // A database as the previous release created it: no users table, no user columns.
        var dataDir = NewDataDir();
        const string token = "old-device-token";
        try
        {
            Directory.CreateDirectory(dataDir);
            var options = new ServerOptions(PrivatePasteFactory.DbKey, "unused", dataDir, 1024, 1024);
            var db = new PrivatePasteDb(options);
            using (var old = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = db.DbPath, Password = options.DbKey, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
            {
                old.Open();
                using var cmd = old.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, platform TEXT NOT NULL,
                      token_hash TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL, last_seen_at TEXT, revoked_at TEXT);
                    CREATE TABLE clips(id TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, text TEXT,
                      hash TEXT NOT NULL UNIQUE, width INTEGER, height INTEGER, size_bytes INTEGER NOT NULL,
                      source_device_id TEXT, source_device_name TEXT NOT NULL, created_at TEXT NOT NULL,
                      last_copied_at TEXT NOT NULL, source_device_platform TEXT, pinned_at TEXT, unpinned_at TEXT);
                    INSERT INTO devices VALUES('11111111-1111-1111-1111-111111111111', 'Old PC', 'windows', @hash, '2025-01-01T00:00:00.0000000Z', NULL, NULL);
                    INSERT INTO clips VALUES('22222222-2222-2222-2222-222222222222', 1, 'text', 'old', 'h1', NULL, NULL, 3, '11111111-1111-1111-1111-111111111111', 'Old PC',
                      '2025-01-01T00:00:00.0000000Z', '2025-01-01T00:00:00.0000000Z', 'windows', NULL, NULL);
                    """;
                cmd.Parameters.AddWithValue("@hash",
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            using var factory = new PrivatePasteFactory(dataDir: dataDir);
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/clips")).StatusCode);

            var owner = (await factory.Services.GetRequiredService<UserRepository>().GetOwnerAsync())!;
            await using var conn = factory.Services.GetRequiredService<PrivatePasteDb>().Open();
            await using var q = conn.CreateCommand();
            q.CommandText = """
                SELECT (SELECT count(*) FROM pragma_table_info('devices') WHERE name = 'user_id'),
                       (SELECT count(*) FROM pragma_table_info('clips') WHERE name = 'source_user_id'),
                       (SELECT user_id FROM devices WHERE id = '11111111-1111-1111-1111-111111111111'),
                       (SELECT source_user_id FROM clips WHERE id = '22222222-2222-2222-2222-222222222222')
                """;
            await using var r = await q.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal(1, r.GetInt32(0));
            Assert.Equal(1, r.GetInt32(1));
            Assert.Equal(owner.Id.ToString(), r.GetString(2));
            Assert.Equal(owner.Id.ToString(), r.GetString(3));
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
