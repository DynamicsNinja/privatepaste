using System.Globalization;
using Microsoft.Data.Sqlite;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Auth;

/// <summary>A person on this server. Removed people keep their row (and name) for the clips they copied.</summary>
public sealed record UserRecord(Guid Id, string Name, bool IsOwner, string PasswordHash, string SecurityStamp,
    DateTimeOffset CreatedAt, DateTimeOffset? RemovedAt);

public sealed class NameTakenException() : Exception("That name is already taken.");

public sealed class UserRepository(PrivatePasteDb db, TimeProvider time)
{
    public const int MaxNameLength = 32;

    private const int SqliteConstraintUnique = 2067; // extended result code: SQLITE_CONSTRAINT_UNIQUE

    private const string Columns = "id, name, is_owner, password_hash, security_stamp, created_at, removed_at";

    /// <summary>The error to show for <paramref name="name"/>, or null when it is valid.</summary>
    public static string? ValidateName(string? name) =>
        name?.Trim() is { Length: > 0 and <= MaxNameLength } ? null : $"Name must be 1-{MaxNameLength} characters.";

    public async Task<UserRecord?> FindCurrentByNameAsync(string name) =>
        await QuerySingleAsync($"SELECT {Columns} FROM users WHERE name_key = @key AND removed_at IS NULL",
            ("@key", NameKey(name)));

    /// <summary>The person with this id, removed or not.</summary>
    public async Task<UserRecord?> GetAsync(Guid id) =>
        await QuerySingleAsync($"SELECT {Columns} FROM users WHERE id = @id", ("@id", id.ToString()));

    public async Task<UserRecord?> GetOwnerAsync() =>
        await QuerySingleAsync($"SELECT {Columns} FROM users WHERE is_owner = 1 AND removed_at IS NULL");

    /// <summary>Current people, the owner first and then by name.</summary>
    public async Task<IReadOnlyList<UserRecord>> ListCurrentAsync()
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM users WHERE removed_at IS NULL ORDER BY is_owner DESC, name_key, id";
        var list = new List<UserRecord>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(Read(r));
        return list;
    }

    public async Task<int> CountCurrentAsync()
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM users WHERE removed_at IS NULL";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <exception cref="NameTakenException">A current person already has this name, ignoring case.</exception>
    public async Task<UserRecord> CreateAsync(string name, string passwordHash, bool isOwner)
    {
        var user = new UserRecord(Guid.NewGuid(), name.Trim(), isOwner, passwordHash, NewStamp(), time.GetUtcNow(), null);
        await ExecuteAsync("""
            INSERT INTO users(id, name, name_key, is_owner, password_hash, security_stamp, created_at)
            VALUES(@id, @name, @key, @owner, @hash, @stamp, @at)
            """,
            ("@id", user.Id.ToString()), ("@name", user.Name), ("@key", NameKey(name)), ("@owner", isOwner ? 1 : 0),
            ("@hash", passwordHash), ("@stamp", user.SecurityStamp), ("@at", Format(user.CreatedAt)));
        return user;
    }

    /// <exception cref="NameTakenException">A current person already has this name, ignoring case.</exception>
    public Task RenameAsync(Guid id, string name) =>
        ExecuteAsync("UPDATE users SET name = @name, name_key = @key WHERE id = @id",
            ("@id", id.ToString()), ("@name", name.Trim()), ("@key", NameKey(name)));

    /// <param name="newSecurityStamp">Ends the person's web sessions (a reset by someone else, or a change they made).</param>
    public Task SetPasswordAsync(Guid id, string passwordHash, bool newSecurityStamp) =>
        ExecuteAsync(newSecurityStamp
                ? "UPDATE users SET password_hash = @hash, security_stamp = @stamp WHERE id = @id"
                : "UPDATE users SET password_hash = @hash WHERE id = @id",
            ("@id", id.ToString()), ("@hash", passwordHash), ("@stamp", NewStamp()));

    /// <summary>
    /// A person's own password change: sets the hash, keeping the stamp, only while the stamp is still
    /// <paramref name="expectedStamp"/> and they are not removed. False when a reset or removal got there first.
    /// </summary>
    public async Task<bool> ChangeOwnPasswordAsync(Guid id, string passwordHash, string expectedStamp)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET password_hash = @hash
            WHERE id = @id AND security_stamp = @stamp AND removed_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@hash", passwordHash);
        cmd.Parameters.AddWithValue("@stamp", expectedStamp);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    /// <summary>Frees the name and, with a new security stamp, ends the person's web sessions.</summary>
    public Task MarkRemovedAsync(Guid id) =>
        ExecuteAsync("UPDATE users SET removed_at = @at, security_stamp = @stamp WHERE id = @id AND removed_at IS NULL",
            ("@id", id.ToString()), ("@at", Format(time.GetUtcNow())), ("@stamp", NewStamp()));

    private async Task<UserRecord?> QuerySingleAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters)
            cmd.Parameters.AddWithValue(n, v);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Read(r) : null;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters)
            cmd.Parameters.AddWithValue(n, v);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            throw new NameTakenException();
        }
    }

    private static UserRecord Read(SqliteDataReader r) =>
        new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt64(2) != 0, r.GetString(3), r.GetString(4),
            Parse(r.GetString(5)), r.IsDBNull(6) ? null : Parse(r.GetString(6)));

    private static string NameKey(string name) => name.Trim().ToLowerInvariant();

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
