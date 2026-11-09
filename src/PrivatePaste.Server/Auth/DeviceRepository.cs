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

    /// <summary>
    /// Adds a device for the person, but only while their security stamp is still <paramref name="securityStamp"/>
    /// (the one read when their password was checked) and they are not removed. Null otherwise: a reset or removal
    /// that landed after the check must not be outlived by a device signed in with the old password.
    /// </summary>
    public async Task<(DeviceDto Device, string Token)?> CreateAsync(string name, string platform, Guid userId,
        string securityStamp, string? appVersion = null)
    {
        var id = Guid.NewGuid();
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();

        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices(id, name, platform, token_hash, created_at, user_id, app_version)
            SELECT @id, @name, @platform, @hash, @at, id, @version FROM users
            WHERE id = @user AND security_stamp = @stamp AND removed_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@platform", platform);
        cmd.Parameters.AddWithValue("@hash", HashToken(token));
        cmd.Parameters.AddWithValue("@at", Format(now));
        cmd.Parameters.AddWithValue("@user", userId.ToString());
        cmd.Parameters.AddWithValue("@stamp", securityStamp);
        cmd.Parameters.AddWithValue("@version", appVersion ?? (object)DBNull.Value);
        if (await cmd.ExecuteNonQueryAsync() == 0)
            return null;

        return (new DeviceDto(id, name, platform, now, null, false) { AppVersion = appVersion }, token);
    }

    /// <summary>The device with this token, unless it is revoked or its person was removed.</summary>
    /// <param name="appVersion">The version the app reports now, saved when it differs from the stored one; null keeps it.</param>
    public async Task<AuthenticatedDevice?> FindByTokenAsync(string token, string? appVersion = null)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.id, d.name, d.platform, d.created_at, d.last_seen_at, d.app_version, u.id, u.is_owner FROM devices d
            JOIN users u ON u.id = d.user_id
            WHERE d.token_hash = @hash AND d.revoked_at IS NULL AND u.removed_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@hash", HashToken(token));
        AuthenticatedDevice? found;
        await using (var r = await cmd.ExecuteReaderAsync())
            found = await r.ReadAsync()
                ? new AuthenticatedDevice(Read(r, null), Guid.Parse(r.GetString(6)), r.GetInt64(7) != 0)
                : null;
        if (found is null)
            return null;

        var device = found.Device;
        var now = time.GetUtcNow();
        var version = appVersion ?? device.AppVersion;
        // An upgrade shows at once, not only when the last-seen time is next due.
        if (device.LastSeenAt is null || now - device.LastSeenAt >= LastSeenInterval || version != device.AppVersion)
        {
            await using var update = conn.CreateCommand();
            update.CommandText = "UPDATE devices SET last_seen_at = @at, app_version = @version WHERE id = @id";
            update.Parameters.AddWithValue("@at", Format(now));
            update.Parameters.AddWithValue("@version", version ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("@id", device.Id.ToString());
            await update.ExecuteNonQueryAsync();
            found = found with { Device = device with { LastSeenAt = now, AppVersion = version } };
        }

        return found;
    }

    /// <param name="userId">Only this person's devices; null lists everyone's.</param>
    public async Task<IReadOnlyList<DeviceDto>> ListAsync(Guid? currentId, Guid? userId = null)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, platform, created_at, last_seen_at, app_version FROM devices
            WHERE revoked_at IS NULL AND (@user IS NULL OR user_id = @user) ORDER BY created_at, id
            """;
        cmd.Parameters.AddWithValue("@user", userId?.ToString() ?? (object)DBNull.Value);
        var list = new List<DeviceDto>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Read(r, currentId));
        return list;
    }

    /// <param name="userId">Only revokes the device when it belongs to this person; null revokes any device.</param>
    public async Task<bool> RevokeAsync(Guid id, Guid? userId = null)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE devices SET revoked_at = @at
            WHERE id = @id AND revoked_at IS NULL AND (@user IS NULL OR user_id = @user)
            """;
        cmd.Parameters.AddWithValue("@at", Format(time.GetUtcNow()));
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@user", userId?.ToString() ?? (object)DBNull.Value);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    /// <summary>Revokes all of a person's devices and returns their ids (so their connections can be closed).</summary>
    public async Task<IReadOnlyList<Guid>> RevokeAllForUserAsync(Guid userId)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE devices SET revoked_at = @at WHERE user_id = @user AND revoked_at IS NULL RETURNING id
            """;
        cmd.Parameters.AddWithValue("@at", Format(time.GetUtcNow()));
        cmd.Parameters.AddWithValue("@user", userId.ToString());
        var ids = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            ids.Add(Guid.Parse(r.GetString(0)));
        return ids;
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
            r.IsDBNull(4) ? null : Parse(r.GetString(4)), id == currentId)
        {
            AppVersion = r.IsDBNull(5) ? null : r.GetString(5),
        };
    }

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
