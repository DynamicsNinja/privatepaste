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
    public void Initialize_adds_and_backfills_the_platform_column_on_an_existing_database()
    {
        // The clips/devices tables as the first release created them, before source_device_platform existed.
        Directory.CreateDirectory(_dir);
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = DbPath, Password = KeyA, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, platform TEXT NOT NULL,
                  token_hash TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL, last_seen_at TEXT, revoked_at TEXT);
                CREATE TABLE clips(id TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, text TEXT,
                  hash TEXT NOT NULL UNIQUE, width INTEGER, height INTEGER, size_bytes INTEGER NOT NULL,
                  source_device_id TEXT, source_device_name TEXT NOT NULL, created_at TEXT NOT NULL,
                  last_copied_at TEXT NOT NULL);
                INSERT INTO devices VALUES('d-win', 'PC', 'windows', 't1', '2026-01-01T00:00:00Z', NULL, NULL);
                INSERT INTO devices VALUES('d-and', 'Phone', 'Android', 't2', '2026-01-01T00:00:00Z', NULL,
                  '2026-01-02T00:00:00Z');
                INSERT INTO clips VALUES('c1', 1, 'text', 'a', 'h1', NULL, NULL, 1, 'd-win', 'PC', 'x', 'x');
                INSERT INTO clips VALUES('c2', 2, 'text', 'b', 'h2', NULL, NULL, 1, 'd-and', 'Phone', 'x', 'x');
                INSERT INTO clips VALUES('c3', 3, 'text', 'c', 'h3', NULL, NULL, 1, NULL, 'Web', 'x', 'x');
                INSERT INTO clips VALUES('c4', 4, 'text', 'd', 'h4', NULL, NULL, 1, 'd-gone', 'Old', 'x', 'x');
                INSERT INTO clips VALUES('c5', 5, 'text', 'e', 'h5', NULL, NULL, 1, NULL, 'Other', 'x', 'x');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = NewDb(KeyA);
        db.Initialize();

        Dictionary<string, string?> Platforms()
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, source_device_platform FROM clips";
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, string?>();
            while (r.Read())
                map[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
            return map;
        }

        var expected = new Dictionary<string, string?>
        {
            ["c1"] = "windows",
            ["c2"] = "android", // revoked device, platform lowercased
            ["c3"] = "web",
            ["c4"] = null, // device row missing: unknown
            ["c5"] = null,
        };
        Assert.Equal(expected, Platforms());

        // Backfill runs once: a later change is not overwritten by another Initialize.
        using (var conn = db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clips SET source_device_platform = 'linux' WHERE id = 'c1'";
            cmd.ExecuteNonQuery();
        }
        db.Initialize();
        Assert.Equal("linux", Platforms()["c1"]);
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
