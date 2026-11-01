using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrivatePaste.Protocol;

namespace PrivatePaste.Server.Storage;

/// <summary>
/// The passphrase-wrapped vault key: at most one row. The server never opens the wrap; it only stores and serves it,
/// and the wrap's key id is what tells a client whether the key it holds is the current one.
/// </summary>
public sealed class VaultRepository(PrivatePasteDb db)
{
    public async Task<VaultWrapDto?> GetAsync()
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT wrap_json FROM vault WHERE id = 1";
        return await cmd.ExecuteScalarAsync() is string json
            ? JsonSerializer.Deserialize<VaultWrapDto>(json, ProtocolJson.Options)
            : null;
    }

    /// <summary>Stores the first wrap. False when a vault already exists.</summary>
    public async Task<bool> CreateAsync(VaultWrapDto wrap)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO vault(id, wrap_json, key_id) VALUES(1, @json, @kid)";
        Bind(cmd, wrap);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>Replaces the wrap of the same key (a new passphrase). False when there is no vault or the key id differs.</summary>
    public async Task<bool> ReplaceWrapAsync(VaultWrapDto wrap)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE vault SET wrap_json = @json WHERE id = 1 AND key_id = @kid";
        Bind(cmd, wrap);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>Stores the wrap whatever was there, key id included (the reset).</summary>
    public async Task ReplaceAllAsync(VaultWrapDto wrap)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO vault(id, wrap_json, key_id) VALUES(1, @json, @kid) " +
                          "ON CONFLICT(id) DO UPDATE SET wrap_json = excluded.wrap_json, key_id = excluded.key_id";
        Bind(cmd, wrap);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void Bind(SqliteCommand cmd, VaultWrapDto wrap)
    {
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(wrap, ProtocolJson.Options));
        cmd.Parameters.AddWithValue("@kid", wrap.KeyId);
    }
}
