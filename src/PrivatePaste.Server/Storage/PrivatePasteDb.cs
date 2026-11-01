using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PrivatePaste.Server.Config;

namespace PrivatePaste.Server.Storage;

public sealed class DatabaseKeyMismatchException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class PrivatePasteDb(ServerOptions options, ILogger<PrivatePasteDb>? logger = null)
{
    static PrivatePasteDb() => SQLitePCL.Batteries_V2.Init();

    // Set by MigrateToE2e in its transaction and removed once the pre-E2E blob files are gone.
    private const string BlobWipePending = "e2e_blob_wipe_pending";

    private const int SqliteNotADb = 26; // what a wrong key looks like: the file doesn't decrypt to a database

    public string DbPath { get; } = Path.Combine(options.DataDir, "privatepaste.db");

    /// <summary>How blob files are removed; the tests swap it to fail on any platform.</summary>
    public Action<string> DeleteBlobFile { get; init; } = File.Delete;

    public string BlobDir { get; } = Path.Combine(options.DataDir, "blobs");

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DbPath,
        Password = options.DbKey,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        try
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // temp_store=MEMORY: sorts and temp tables never spill to (unencrypted) temp files.
            cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;";
            cmd.ExecuteNonQuery();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public void Initialize()
    {
        Directory.CreateDirectory(options.DataDir);
        Directory.CreateDirectory(BlobDir);

        var existed = File.Exists(DbPath);
        using var conn = new SqliteConnection(ConnectionString);
        try
        {
            conn.Open();
            if (existed)
            {
                using var probe = conn.CreateCommand();
                probe.CommandText = "SELECT count(*) FROM sqlite_master";
                probe.ExecuteScalar();
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADb)
        {
            throw new DatabaseKeyMismatchException(
                $"PP_DB_KEY does not match the existing database at {DbPath}", ex);
        }

        using var schema = conn.CreateCommand();
        schema.CommandText = "PRAGMA temp_store=MEMORY; " + ReadSchema();
        schema.ExecuteNonQuery();

        Migrate(conn);
        MigrateToE2e(conn);
        WipePendingBlobs(conn);
    }

    /// <summary>
    /// Brings a database created by an older version up to the current schema. Each step is idempotent: it checks
    /// whether its change is already there, and runs in one transaction with its backfill, so a crash never leaves a
    /// column added but not backfilled.
    /// </summary>
    private static void Migrate(SqliteConnection conn)
    {
        if (!HasColumn(conn, "clips", "source_device_platform"))
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // Backfill once, from the device that last copied each clip (revoked devices keep their row and
            // platform). The web UI is not a device: it copies as (null, "Web"). Anything else stays NULL (unknown).
            cmd.CommandText = """
                ALTER TABLE clips ADD COLUMN source_device_platform TEXT;
                UPDATE clips SET source_device_platform =
                    (SELECT lower(trim(d.platform)) FROM devices d WHERE d.id = clips.source_device_id)
                    WHERE source_device_id IS NOT NULL;
                UPDATE clips SET source_device_platform = 'web'
                    WHERE source_device_id IS NULL AND source_device_name = 'Web';
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        if (!HasColumn(conn, "clips", "pinned_at"))
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                ALTER TABLE clips ADD COLUMN pinned_at TEXT;
                ALTER TABLE clips ADD COLUMN unpinned_at TEXT;
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        // Who each device and clip belongs to. Existing rows get the owner from OwnerBootstrap, which runs after this.
        AddColumnIfMissing(conn, "devices", "user_id");
        AddColumnIfMissing(conn, "clips", "source_user_id");

        // Here, not in Schema.sql: that script runs before this method, when an old database has no pinned_at yet.
        using var index = conn.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS ix_clips_pinned ON clips(pinned_at DESC, seq DESC) " +
                            "WHERE pinned_at IS NOT NULL";
        index.ExecuteNonQuery();
    }

    /// <summary>
    /// The upgrade to end-to-end encryption. History from before it is plaintext on the server and can't be sealed
    /// without a key, so it is wiped: clips, tombstones, the search index and the blob files, plus the clear marker
    /// that only meant something for that history. Users, devices and other settings stay, and so does the seq
    /// counter, so a client that remembers an older seq never sees one reused. Runs only while <c>clips</c> has no
    /// <c>payload</c> column, which the same transaction adds, so it happens once. The blob files are deleted by
    /// <see cref="WipePendingBlobs"/>, driven by a marker this transaction sets, so a crash can't strand them.
    /// </summary>
    private static void MigrateToE2e(SqliteConnection conn)
    {
        if (HasColumn(conn, "clips", "payload"))
            return;

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // The search index goes first, so deleting the rows doesn't touch it (a damaged index can't block startup).
        cmd.CommandText = $"""
            DROP TRIGGER IF EXISTS clips_ai;
            DROP TRIGGER IF EXISTS clips_ad;
            DROP TRIGGER IF EXISTS clips_au;
            DROP TABLE IF EXISTS clips_fts;
            DELETE FROM clips;
            DELETE FROM tombstones;
            ALTER TABLE clips ADD COLUMN payload TEXT;
            ALTER TABLE clips ADD COLUMN key_id TEXT;
            ALTER TABLE clips ADD COLUMN has_thumb INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE clips ADD COLUMN blob_count INTEGER NOT NULL DEFAULT 0;
            DELETE FROM settings WHERE name = '{SettingsRepository.HistoryClearedAt}';
            INSERT OR REPLACE INTO settings(name, value) VALUES('{BlobWipePending}', '1');
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>
    /// Deletes the blob files while the marker from <see cref="MigrateToE2e"/> is set, on every start, and clears the
    /// marker only when every file is gone. A file that can't be deleted (locked on Windows) is logged by name and
    /// retried at the next start; it never stops the server from starting.
    /// </summary>
    private void WipePendingBlobs(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT count(*) FROM settings WHERE name = @name";
        check.Parameters.AddWithValue("@name", BlobWipePending);
        if ((long)check.ExecuteScalar()! == 0)
            return;

        var failed = 0;
        foreach (var file in Directory.GetFiles(BlobDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                DeleteBlobFile(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                logger?.LogWarning(ex, "Could not delete pre-encryption blob {File}; will retry at the next start",
                    Path.GetFileName(file));
            }
        }

        if (failed > 0)
            return;
        using var clear = conn.CreateCommand();
        clear.CommandText = "DELETE FROM settings WHERE name = @name";
        clear.Parameters.AddWithValue("@name", BlobWipePending);
        clear.ExecuteNonQuery();
    }

    // For a nullable TEXT column with no backfill; table and column are constants, never input.
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column)
    {
        if (HasColumn(conn, table, column))
            return;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT";
        cmd.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info(@table) WHERE name = @column";
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@column", column);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static string ReadSchema()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("Schema.sql", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
