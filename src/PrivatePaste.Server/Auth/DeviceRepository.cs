using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Auth;

public sealed class DeviceRepository(PrivatePasteDb db, TimeProvider time)
{
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromMinutes(1);

    public async Task<(DeviceDto Device, string Token)> CreateAsync(string name, string platform)
    {
        var id = Guid.NewGuid();
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();

        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices(id, name, platform, token_hash, created_at)
            VALUES(@id, @name, @platform, @hash, @at)
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@platform", platform);
        cmd.Parameters.AddWithValue("@hash", HashToken(token));
        cmd.Parameters.AddWithValue("@at", Format(now));
        await cmd.ExecuteNonQueryAsync();

        return (new DeviceDto(id, name, platform, now, null, false), token);
    }

    public async Task<DeviceDto?> FindByTokenAsync(string token)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, platform, created_at, last_seen_at FROM devices
            WHERE token_hash = @hash AND revoked_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@hash", HashToken(token));
        DeviceDto? device;
        await using (var r = await cmd.ExecuteReaderAsync())
            device = await r.ReadAsync() ? Read(r, null) : null;
        if (device is null)
            return null;

        var now = time.GetUtcNow();
        if (device.LastSeenAt is null || now - device.LastSeenAt >= LastSeenInterval)
        {
            await using var update = conn.CreateCommand();
            update.CommandText = "UPDATE devices SET last_seen_at = @at WHERE id = @id";
            update.Parameters.AddWithValue("@at", Format(now));
            update.Parameters.AddWithValue("@id", device.Id.ToString());
            await update.ExecuteNonQueryAsync();
            device = device with { LastSeenAt = now };
        }

        return device;
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
