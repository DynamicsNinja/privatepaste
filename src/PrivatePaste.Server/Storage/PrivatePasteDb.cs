using System.Reflection;
using Microsoft.Data.Sqlite;
using PrivatePaste.Server.Config;

namespace PrivatePaste.Server.Storage;

public sealed class DatabaseKeyMismatchException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class PrivatePasteDb(ServerOptions options)
{
    static PrivatePasteDb() => SQLitePCL.Batteries_V2.Init();

    private const int SqliteNotADb = 26; // what a wrong key looks like: the file doesn't decrypt to a database

    public string DbPath { get; } = Path.Combine(options.DataDir, "privatepaste.db");

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
