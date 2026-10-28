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
