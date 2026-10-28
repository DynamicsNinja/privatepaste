using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Storage;

public sealed class PrivatePasteDbTests : IDisposable
{
    private const string KeyA = "key-A-0123456789abcdef";
    private const string KeyB = "key-B-0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "privatepaste-db-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "privatepaste.db");

    private PrivatePasteDb NewDb(string key) =>
        new(new ServerOptions(key, "pw", _dir, 1024, 1024));

    private static void InsertClip(SqliteConnection conn, string text)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO clips(id, seq, kind, text, hash, size_bytes, source_device_name, created_at, last_copied_at)
            VALUES('c1', 1, 'text', $t, 'h1', 10, 'dev', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z')
            """;
        cmd.Parameters.AddWithValue("$t", text);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Fts5_trigram_available()
    {
        var db = NewDb(KeyA);
        db.Initialize();
        using var conn = db.Open();
        InsertClip(conn, "a piece of text");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM clips_fts WHERE clips_fts MATCH '\"ece\"'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void File_contains_no_plaintext()
    {
        var db = NewDb(KeyA);
        db.Initialize();
        using (var conn = db.Open())
            InsertClip(conn, "supersecret-canary");
        SqliteConnection.ClearAllPools();

        var bytes = File.ReadAllBytes(DbPath);
        Assert.DoesNotContain("supersecret-canary", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("SQLite format 3", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Wrong_key_throws_and_leaves_file_untouched()
    {
        var db = NewDb(KeyA);
        db.Initialize();
        using (var conn = db.Open())
            InsertClip(conn, "hello");
        SqliteConnection.ClearAllPools();
        var before = SHA256.HashData(File.ReadAllBytes(DbPath));

        Assert.Throws<DatabaseKeyMismatchException>(() => NewDb(KeyB).Initialize());
        SqliteConnection.ClearAllPools();

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(DbPath)));
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        var db = NewDb(KeyA);
        db.Initialize();
        db.Initialize();
    }

    [Fact]
    public void Open_enables_foreign_keys_and_creates_blob_dir()
    {
        var db = NewDb(KeyA);
        db.Initialize();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        Assert.True(Directory.Exists(Path.Combine(_dir, "blobs")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    [Fact]
    public void Open_keeps_temp_storage_in_memory()
    {
        var db = NewDb(KeyA);
        db.Initialize();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA temp_store";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!); // 2 = MEMORY
    }

    [Fact]
    public void Initialize_does_not_report_other_errors_as_key_mismatch()
    {
        Directory.CreateDirectory(DbPath); // a directory where the database file should be: SQLITE_CANTOPEN
        var ex = Assert.Throws<SqliteException>(() => NewDb(KeyA).Initialize());
        Assert.NotEqual(26, ex.SqliteErrorCode);
    }

}
