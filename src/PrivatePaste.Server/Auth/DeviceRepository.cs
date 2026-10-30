using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Auth;

/// <summary>A device found by its token, with the person it belongs to.</summary>
public sealed record AuthenticatedDevice(DeviceDto Device, Guid UserId, bool IsOwner);

public sealed class DeviceRepository(PrivatePasteDb db, TimeProvider time)
{
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromMinutes(1);

    public async Task<(DeviceDto Device, string Token)> CreateAsync(string name, string platform, Guid userId)
    {
        var id = Guid.NewGuid();
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();

        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices(id, name, platform, token_hash, created_at, user_id)
            VALUES(@id, @name, @platform, @hash, @at, @user)
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@platform", platform);
        cmd.Parameters.AddWithValue("@hash", HashToken(token));
        cmd.Parameters.AddWithValue("@at", Format(now));
        cmd.Parameters.AddWithValue("@user", userId.ToString());
        await cmd.ExecuteNonQueryAsync();

        return (new DeviceDto(id, name, platform, now, null, false), token);
    }

    /// <summary>The device with this token, unless it is revoked or its person was removed.</summary>
    public async Task<AuthenticatedDevice?> FindByTokenAsync(string token)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.id, d.name, d.platform, d.created_at, d.last_seen_at, u.id, u.is_owner FROM devices d
            JOIN users u ON u.id = d.user_id
            WHERE d.token_hash = @hash AND d.revoked_at IS NULL AND u.removed_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@hash", HashToken(token));
        AuthenticatedDevice? found;
        await using (var r = await cmd.ExecuteReaderAsync())
            found = await r.ReadAsync()
                ? new AuthenticatedDevice(Read(r, null), Guid.Parse(r.GetString(5)), r.GetInt64(6) != 0)
                : null;
        if (found is null)
            return null;

        var device = found.Device;
        var now = time.GetUtcNow();
        if (device.LastSeenAt is null || now - device.LastSeenAt >= LastSeenInterval)
        {
            await using var update = conn.CreateCommand();
            update.CommandText = "UPDATE devices SET last_seen_at = @at WHERE id = @id";
            update.Parameters.AddWithValue("@at", Format(now));
            update.Parameters.AddWithValue("@id", device.Id.ToString());
            await update.ExecuteNonQueryAsync();
            found = found with { Device = device with { LastSeenAt = now } };
        }

        return found;
    }

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(Guid? currentId)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, platform, created_at, last_seen_at FROM devices
            WHERE revoked_at IS NULL ORDER BY created_at, id
            """;
        var list = new List<DeviceDto>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Read(r, currentId));
        return list;
    }

    public async Task<bool> RevokeAsync(Guid id)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE devices SET revoked_at = @at WHERE id = @id AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("@at", Format(time.GetUtcNow()));
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> IsActiveAsync(Guid id)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM devices WHERE id = @id AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static DeviceDto Read(SqliteDataReader r, Guid? currentId)
    {
        var id = Guid.Parse(r.GetString(0));
        return new DeviceDto(id, r.GetString(1), r.GetString(2), Parse(r.GetString(3)),
            r.IsDBNull(4) ? null : Parse(r.GetString(4)), id == currentId);
    }

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
