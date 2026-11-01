using Microsoft.Data.Sqlite;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Storage;

public sealed class E2eMigrationTests : IDisposable
{
    private const string Key = "key-0123456789abcdef";

    // Schema.sql as it was before end-to-end encryption.
    private const string OldSchema = """
CREATE TABLE IF NOT EXISTS counters(name TEXT PRIMARY KEY, value INTEGER NOT NULL);

-- People. name_key is the trimmed, lowercased name; removed people keep their row so their clips keep a name.
CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL,
  is_owner INTEGER NOT NULL, password_hash TEXT NOT NULL, security_stamp TEXT NOT NULL, created_at TEXT NOT NULL,
  removed_at TEXT);

CREATE UNIQUE INDEX IF NOT EXISTS ux_users_name ON users(name_key) WHERE removed_at IS NULL;

CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, platform TEXT NOT NULL,
  token_hash TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL, last_seen_at TEXT, revoked_at TEXT, user_id TEXT);

CREATE TABLE IF NOT EXISTS clips(id TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, text TEXT,
  hash TEXT NOT NULL UNIQUE, width INTEGER, height INTEGER, size_bytes INTEGER NOT NULL,
  source_device_id TEXT, source_device_name TEXT NOT NULL, created_at TEXT NOT NULL, last_copied_at TEXT NOT NULL,
  source_device_platform TEXT, pinned_at TEXT, unpinned_at TEXT, source_user_id TEXT);
-- Columns added after the first release are also added to existing databases by PrivatePasteDb.Migrate.

CREATE INDEX IF NOT EXISTS ix_clips_order ON clips(last_copied_at DESC, seq DESC);

CREATE VIRTUAL TABLE IF NOT EXISTS clips_fts USING fts5(text, content='clips', content_rowid='rowid', tokenize='trigram');

CREATE TRIGGER IF NOT EXISTS clips_ai AFTER INSERT ON clips BEGIN
  INSERT INTO clips_fts(rowid, text) VALUES (new.rowid, new.text);
END;

CREATE TRIGGER IF NOT EXISTS clips_ad AFTER DELETE ON clips BEGIN
  INSERT INTO clips_fts(clips_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
END;

CREATE TRIGGER IF NOT EXISTS clips_au AFTER UPDATE OF text ON clips BEGIN
  INSERT INTO clips_fts(clips_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
  INSERT INTO clips_fts(rowid, text) VALUES (new.rowid, new.text);
END;

CREATE TABLE IF NOT EXISTS tombstones(clip_id TEXT PRIMARY KEY, seq INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS settings(name TEXT PRIMARY KEY, value TEXT NOT NULL);
""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "privatepaste-e2e-" + Guid.NewGuid().ToString("N"));

    private PrivatePasteDb NewDb() => new(new ServerOptions(Key, "pw", _dir, 1024, 1024));

    private string BlobDir => Path.Combine(_dir, "blobs");

    private SqliteConnection OpenRaw()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(_dir, "privatepaste.db"), Password = Key, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static string? Text(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private void SeedOldDatabase()
    {
        Directory.CreateDirectory(BlobDir);
        using (var conn = OpenRaw())
        {
            Exec(conn, OldSchema);
            Exec(conn, """
                INSERT INTO users VALUES('u1', 'Ivan', 'ivan', 1, 'pw-hash', 'stamp', '2026-01-01T00:00:00Z', NULL);
                INSERT INTO devices VALUES('d1', 'PC', 'windows', 'tok', '2026-01-01T00:00:00Z', NULL, NULL, 'u1');
                INSERT INTO clips(id, seq, kind, text, hash, size_bytes, source_device_id, source_device_name,
                                  created_at, last_copied_at, pinned_at)
                  VALUES('c1', 5, 'text', 'plain secret', 'h1', 12, 'd1', 'PC', '2026-01-01T00:00:00Z',
                         '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z');
                INSERT INTO clips(id, seq, kind, hash, width, height, size_bytes, source_device_name,
                                  created_at, last_copied_at)
                  VALUES('c2', 6, 'image', 'h2', 1, 1, 100, 'PC', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                INSERT INTO tombstones VALUES('gone', 7);
                INSERT INTO counters VALUES('seq', 7);
                INSERT INTO settings VALUES('history_cleared_at', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO settings VALUES('auto_delete_days', '30');
                """);
        }

        File.WriteAllBytes(Path.Combine(BlobDir, "c2.enc"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(BlobDir, "c2.thumb.enc"), [4, 5]);
        SqliteConnection.ClearAllPools();
    }

    // secure_delete zeroes freed pages whatever the native build's default is. The file is encrypted at rest, so this
    // looks at an unencrypted one (no key): the deleted clip's text, its search index included, must be gone from it.
    [Fact]
    public void OldDatabase_WipeLeavesNoDeletedTextInTheFile()
    {
        var needle = "plain secret needle 7f3c"u8.ToArray();
        var path = Path.Combine(_dir, "privatepaste.db");
        Directory.CreateDirectory(BlobDir);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            Exec(conn, "PRAGMA secure_delete=OFF;");
            Exec(conn, OldSchema);
            Exec(conn, """
                INSERT INTO clips(id, seq, kind, text, hash, size_bytes, source_device_name, created_at, last_copied_at)
                  VALUES('c1', 5, 'text', 'plain secret needle 7f3c', 'h1', 24, 'PC', '2026-01-01T00:00:00Z',
                         '2026-01-01T00:00:00Z');
                """);
        }
        SqliteConnection.ClearAllPools();
        Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(needle) >= 0);

        new PrivatePasteDb(new ServerOptions("", "pw", _dir, 1024, 1024)).Initialize();
        SqliteConnection.ClearAllPools();

        foreach (var file in Directory.GetFiles(_dir, "privatepaste.db*"))
            Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf(needle) < 0, $"{Path.GetFileName(file)} holds the deleted text");
    }

    [Fact]
    public void OldDatabase_IsWipedOnUpgrade()
    {
        SeedOldDatabase();

        NewDb().Initialize();

        using var conn = OpenRaw();
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM clips"));
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM tombstones"));
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM sqlite_master WHERE name LIKE 'clips_fts%'"));
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('clips_ai', 'clips_ad', 'clips_au')"));
        Assert.Empty(Directory.GetFiles(BlobDir));
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM settings WHERE name = 'e2e_blob_wipe_pending'"));

        Assert.Equal(1, Count(conn, "SELECT count(*) FROM users"));
        Assert.Equal(1, Count(conn, "SELECT count(*) FROM devices"));
        // Other settings stay; the clear marker goes with the history it described.
        Assert.Equal("30", Text(conn, "SELECT value FROM settings WHERE name = 'auto_delete_days'"));
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM settings WHERE name = 'history_cleared_at'"));
        // The counter keeps counting, so no old seq is ever reused.
        Assert.Equal(7, Count(conn, "SELECT value FROM counters WHERE name = 'seq'"));
    }

    [Fact]
    public async Task OldDatabase_NextClipContinuesTheSeqCounter()
    {
        SeedOldDatabase();
        var db = NewDb();
        db.Initialize();
        var repo = new ClipRepository(db);

        var created = await repo.UpsertAsync(new NewClip(ClipKind.Text, null, "h-new", null, null, 3,
            null, "PC", "windows", DateTimeOffset.UtcNow, Payload: "AQID", KeyId: "0011223344556677", HasThumb: true, BlobCount: 2));

        Assert.Equal(8, created.Clip.Seq);
        Assert.Equal("AQID", created.Clip.Payload);
        Assert.Equal("0011223344556677", created.Clip.KeyId);
        Assert.True(created.Clip.HasThumb);
        Assert.Equal(2, created.Clip.BlobCount);
        var listed = Assert.Single((await repo.ListAsync(null, 50)).Items);
        Assert.Equal(created.Clip.Id, listed.Id);
    }

    [Fact]
    public void Upgrade_IsIdempotent()
    {
        SeedOldDatabase();
        var db = NewDb();
        db.Initialize();
        using (var conn = OpenRaw())
        {
            Exec(conn, """
                INSERT INTO clips(id, seq, kind, hash, size_bytes, source_device_name, created_at, last_copied_at, payload, key_id)
                VALUES('c3', 8, 'text', 'h3', 1, 'PC', '2026-02-01T00:00:00Z', '2026-02-01T00:00:00Z', 'AQID', 'k');
                INSERT INTO settings VALUES('history_cleared_at', '2026-02-01T00:00:00.0000000Z');
                """);
        }
        File.WriteAllBytes(Path.Combine(BlobDir, "c3.enc"), [9]);
        SqliteConnection.ClearAllPools();

        db.Initialize();
        db.Initialize();

        using var after = OpenRaw();
        Assert.Equal(1, Count(after, "SELECT count(*) FROM clips"));
        Assert.Equal(1, Count(after, "SELECT count(*) FROM settings WHERE name = 'history_cleared_at'"));
        Assert.Single(Directory.GetFiles(BlobDir));
    }

    [Fact]
    public void FirstReleaseDatabase_GoesThroughTheWholeMigrationChain()
    {
        Directory.CreateDirectory(BlobDir);
        using (var conn = OpenRaw())
        {
            // The first release: no platform, pin or user columns, no users table, with the search index.
            Exec(conn, """
                CREATE TABLE devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, platform TEXT NOT NULL,
                  token_hash TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL, last_seen_at TEXT, revoked_at TEXT);
                CREATE TABLE clips(id TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, text TEXT,
                  hash TEXT NOT NULL UNIQUE, width INTEGER, height INTEGER, size_bytes INTEGER NOT NULL,
                  source_device_id TEXT, source_device_name TEXT NOT NULL, created_at TEXT NOT NULL,
                  last_copied_at TEXT NOT NULL);
                CREATE VIRTUAL TABLE clips_fts USING fts5(text, content='clips', content_rowid='rowid', tokenize='trigram');
                CREATE TRIGGER clips_ai AFTER INSERT ON clips BEGIN
                  INSERT INTO clips_fts(rowid, text) VALUES (new.rowid, new.text);
                END;
                CREATE TRIGGER clips_ad AFTER DELETE ON clips BEGIN
                  INSERT INTO clips_fts(clips_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
                END;
                CREATE TRIGGER clips_au AFTER UPDATE OF text ON clips BEGIN
                  INSERT INTO clips_fts(clips_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
                  INSERT INTO clips_fts(rowid, text) VALUES (new.rowid, new.text);
                END;
                CREATE TABLE tombstones(clip_id TEXT PRIMARY KEY, seq INTEGER NOT NULL);
                CREATE TABLE counters(name TEXT PRIMARY KEY, value INTEGER NOT NULL);
                INSERT INTO devices VALUES('d1', 'PC', 'windows', 't1', '2026-01-01T00:00:00Z', NULL, NULL);
                INSERT INTO clips VALUES('c1', 1, 'text', 'first release text', 'h1', NULL, NULL, 18, 'd1', 'PC', 'x', 'x');
                INSERT INTO tombstones VALUES('gone', 2);
                INSERT INTO counters VALUES('seq', 2);
                """);
        }

        File.WriteAllBytes(Path.Combine(BlobDir, "c1.enc"), [1]);
        SqliteConnection.ClearAllPools();

        NewDb().Initialize();

        using var after = OpenRaw();
        Assert.Equal(0, Count(after, "SELECT count(*) FROM clips"));
        Assert.Equal(0, Count(after, "SELECT count(*) FROM tombstones"));
        Assert.Equal(0, Count(after, "SELECT count(*) FROM sqlite_master WHERE name LIKE 'clips_fts%'"));
        Assert.Equal(1, Count(after, "SELECT count(*) FROM devices"));
        Assert.Equal(2, Count(after, "SELECT value FROM counters WHERE name = 'seq'"));
        Assert.Equal(4, Count(after, "SELECT count(*) FROM pragma_table_info('clips') WHERE name IN ('payload', 'pinned_at', 'source_user_id', 'source_device_platform')"));
        Assert.Empty(Directory.GetFiles(BlobDir));
        Assert.Equal(0, Count(after, "SELECT count(*) FROM settings WHERE name = 'e2e_blob_wipe_pending'"));
    }

    [Fact]
    public void PendingBlobWipe_RunsOnEveryStartAndClearsItsMarker()
    {
        NewDb().Initialize();
        using (var conn = OpenRaw())
            Exec(conn, "INSERT INTO settings VALUES('e2e_blob_wipe_pending', '1')");
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(Path.Combine(BlobDir, "left-behind.enc"), [1]);

        NewDb().Initialize();

        Assert.Empty(Directory.GetFiles(BlobDir));
        using var after = OpenRaw();
        Assert.Equal(0, Count(after, "SELECT count(*) FROM settings WHERE name = 'e2e_blob_wipe_pending'"));
    }

    [Fact]
    public void PendingBlobWipe_KeepsItsMarkerWhenADeleteFailsAndRetriesNextStart()
    {
        NewDb().Initialize();
        using (var conn = OpenRaw())
            Exec(conn, "INSERT INTO settings VALUES('e2e_blob_wipe_pending', '1')");
        SqliteConnection.ClearAllPools();
        var stuck = Path.Combine(BlobDir, "stuck.enc");
        File.WriteAllBytes(stuck, [1]);
        File.WriteAllBytes(Path.Combine(BlobDir, "free.enc"), [2]);

        var failing = new PrivatePasteDb(new ServerOptions(Key, "pw", _dir, 1024, 1024))
        {
            DeleteBlobFile = f => { if (Path.GetFileName(f) == "stuck.enc") throw new IOException("locked"); File.Delete(f); },
        };
        failing.Initialize(); // does not throw: a stuck file never blocks startup

        Assert.Equal(["stuck.enc"], Directory.GetFiles(BlobDir).Select(Path.GetFileName));
        using (var mid = OpenRaw())
            Assert.Equal(1, Count(mid, "SELECT count(*) FROM settings WHERE name = 'e2e_blob_wipe_pending'"));
        SqliteConnection.ClearAllPools();

        NewDb().Initialize();

        Assert.Empty(Directory.GetFiles(BlobDir));
        using var after = OpenRaw();
        Assert.Equal(0, Count(after, "SELECT count(*) FROM settings WHERE name = 'e2e_blob_wipe_pending'"));
    }

    [Fact]
    public void NewDatabase_HasE2eColumns()
    {
        NewDb().Initialize();

        using var conn = OpenRaw();
        var columns = new Dictionary<string, (string Type, long NotNull, string? Default)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('clips')";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                columns[r.GetString(0)] = (r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3));
        }

        Assert.Equal(("TEXT", 0L, null), columns["payload"]);
        Assert.Equal(("TEXT", 0L, null), columns["key_id"]);
        Assert.Equal(("INTEGER", 1L, "0"), columns["has_thumb"]);
        Assert.Equal(("INTEGER", 1L, "0"), columns["blob_count"]);
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM sqlite_master WHERE name LIKE 'clips_fts%'"));
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM vault"));
        Assert.Equal(0, Count(conn, "SELECT count(*) FROM handovers"));
        Assert.Throws<SqliteException>(() => Exec(conn, "INSERT INTO vault VALUES(2, '{}', 'k')"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
