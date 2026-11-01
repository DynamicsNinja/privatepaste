using System.Globalization;
using PrivatePaste.Protocol;

namespace PrivatePaste.Server.Storage;

/// <summary>
/// One-time device handovers: a small blob (the vault key under a one-time key) that one signed-in device leaves
/// for another to pick up. It can be taken once and expires after ten minutes; the server never sees the one-time key.
/// </summary>
public sealed class HandoverStore(PrivatePasteDb db, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public async Task<HandoverCreated> CreateAsync(byte[] blob)
    {
        var now = time.GetUtcNow();
        var created = new HandoverCreated(Guid.NewGuid(), now + Lifetime);
        await using var conn = db.Open();

        // Expired rows are only reachable by id after the clock passes them, so purging on create is enough.
        await using (var purge = conn.CreateCommand())
        {
            purge.CommandText = "DELETE FROM handovers WHERE expires_at <= @now";
            purge.Parameters.AddWithValue("@now", Format(now));
            await purge.ExecuteNonQueryAsync();
        }

        await using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO handovers(id, blob, expires_at) VALUES(@id, @blob, @exp)";
        insert.Parameters.AddWithValue("@id", created.Id.ToString("D"));
        insert.Parameters.AddWithValue("@blob", blob);
        insert.Parameters.AddWithValue("@exp", Format(created.ExpiresAt));
        await insert.ExecuteNonQueryAsync();
        return created;
    }

    /// <summary>Returns the blob and deletes it in one statement, so only one caller can ever get it.</summary>
    public async Task<byte[]?> TakeAsync(Guid id)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM handovers WHERE id = @id AND expires_at > @now RETURNING blob";
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        cmd.Parameters.AddWithValue("@now", Format(time.GetUtcNow()));
        return await cmd.ExecuteScalarAsync() as byte[];
    }

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
