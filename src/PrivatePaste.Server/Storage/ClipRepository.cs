using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PrivatePaste.Protocol;

namespace PrivatePaste.Server.Storage;

public sealed record NewClip(ClipKind Kind, string? Text, string Hash, int? Width, int? Height, long SizeBytes,
    Guid? SourceDeviceId, string SourceDeviceName, string? SourceDevicePlatform, DateTimeOffset CopiedAt);

public sealed record UpsertResult(ClipDto Clip, bool Created);

public sealed class ClipRepository(PrivatePasteDb db)
{
    private const string Columns =
        "c.id, c.seq, c.kind, c.text, c.hash, c.width, c.height, c.size_bytes, c.source_device_id, c.source_device_name, c.last_copied_at, " +
        "c.source_device_platform, c.pinned_at";

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<UpsertResult> UpsertAsync(NewClip clip)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = db.Open();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

            var existing = await ScalarAsync<string>(conn, tx, "SELECT id FROM clips WHERE hash = @h", ("@h", clip.Hash));
            UpsertResult result;
            if (existing is not null)
            {
                var bumped = await BumpCoreAsync(conn, tx, Guid.Parse(existing), clip.SourceDeviceId,
                    clip.SourceDeviceName, clip.SourceDevicePlatform, clip.CopiedAt);
                result = new UpsertResult(bumped!, false);
            }
            else
            {
                var id = Guid.NewGuid();
                var seq = await NextSeqAsync(conn, tx);
                var at = Format(clip.CopiedAt);
                await ExecAsync(conn, tx, """
                    INSERT INTO clips(id, seq, kind, text, hash, width, height, size_bytes,
                                      source_device_id, source_device_name, source_device_platform,
                                      created_at, last_copied_at)
                    VALUES(@id, @seq, @kind, @text, @hash, @w, @h, @size, @dev, @devName, @devPlatform, @at, @at)
                    """,
                    ("@id", id.ToString()), ("@seq", seq), ("@kind", KindToDb(clip.Kind)), ("@text", clip.Text),
                    ("@hash", clip.Hash), ("@w", clip.Width), ("@h", clip.Height), ("@size", clip.SizeBytes),
                    ("@dev", clip.SourceDeviceId?.ToString()), ("@devName", clip.SourceDeviceName),
                    ("@devPlatform", clip.SourceDevicePlatform), ("@at", at));
                result = new UpsertResult(await GetCoreAsync(conn, tx, id) ?? throw new InvalidOperationException(), true);
            }

            await tx.CommitAsync();
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ClipDto?> BumpAsync(Guid id, Guid? deviceId, string deviceName, string? devicePlatform,
        DateTimeOffset at)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = db.Open();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
            var dto = await BumpCoreAsync(conn, tx, id, deviceId, deviceName, devicePlatform, at);
            await tx.CommitAsync();
            return dto;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ClipDto?> GetAsync(Guid id)
    {
        await using var conn = db.Open();
        return await GetCoreAsync(conn, null, id);
    }

    public Task<ClipPage> ListAsync(string? query, string? cursor, int limit) =>
        ListCoreAsync(query, cursor, limit, pinnedOnly: false);

    /// <summary>Pinned clips only, newest pinned first. Cursors and limits behave as in <see cref="ListAsync"/>.</summary>
    public Task<ClipPage> ListPinnedAsync(string? query, string? cursor, int limit) =>
        ListCoreAsync(query, cursor, limit, pinnedOnly: true);

    private async Task<ClipPage> ListCoreAsync(string? query, string? cursor, int limit, bool pinnedOnly)
    {
        // The two lists differ only in the column they sort and page by.
        var sortColumn = pinnedOnly ? "c.pinned_at" : "c.last_copied_at";
        limit = Math.Clamp(limit, 1, 200);
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder($"SELECT {Columns} FROM clips c");
        var where = new List<string>();

        if (!string.IsNullOrEmpty(query))
        {
            if (query.EnumerateRunes().Count() >= 3)
            {
                sql.Append(" JOIN clips_fts ON clips_fts.rowid = c.rowid");
                where.Add("clips_fts MATCH @q");
                cmd.Parameters.AddWithValue("@q", "\"" + query.Replace("\"", "\"\"") + "\"");
            }
            else
            {
                where.Add("c.text LIKE '%' || @q || '%' ESCAPE '\\'");
                cmd.Parameters.AddWithValue("@q",
                    query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"));
            }
        }

        if (pinnedOnly)
            where.Add("c.pinned_at IS NOT NULL");

        if (cursor is not null)
        {
            var (at, seq) = DecodeCursor(cursor);
            where.Add($"({sortColumn} < @cAt OR ({sortColumn} = @cAt AND c.seq < @cSeq))");
            cmd.Parameters.AddWithValue("@cAt", Format(at));
            cmd.Parameters.AddWithValue("@cSeq", seq);
        }

        if (where.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        sql.Append($" ORDER BY {sortColumn} DESC, c.seq DESC LIMIT @limit");
        cmd.Parameters.AddWithValue("@limit", limit + 1);
        cmd.CommandText = sql.ToString();

        var items = await ReadClipsAsync(cmd);
        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            next = EncodeCursor(pinnedOnly ? last.PinnedAt!.Value : last.LastCopiedAt, last.Seq);
        }

        return new ClipPage(items, next);
    }

    /// <summary>How many clips "Clear history" would delete right now.</summary>
    public async Task<long> CountUnpinnedAsync()
    {
        await using var conn = db.Open();
        return await ScalarAsync<long>(conn, null!, "SELECT COUNT(*) FROM clips WHERE pinned_at IS NULL");
    }

    public async Task<long?> DeleteAsync(Guid id)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = db.Open();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
            var deleted = await ExecAsync(conn, tx, "DELETE FROM clips WHERE id = @id", ("@id", id.ToString()));
            if (deleted == 0)
                return null;
            var seq = await NextSeqAsync(conn, tx);
            await ExecAsync(conn, tx, "INSERT OR REPLACE INTO tombstones(clip_id, seq) VALUES(@id, @seq)",
                ("@id", id.ToString()), ("@seq", seq));
            await tx.CommitAsync();
            return seq;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ClipDto?> SetPinnedAsync(Guid id, bool pinned, DateTimeOffset at)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = db.Open();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
            var current = await GetCoreAsync(conn, tx, id);
            if (current is null)
                return null;
            if ((current.PinnedAt is not null) == pinned)
                return current;

            // Pinning is a change clients must sync, so it takes a new seq; last_copied_at stays as it was.
            var seq = await NextSeqAsync(conn, tx);
            await ExecAsync(conn, tx, """
                UPDATE clips SET seq = @seq, pinned_at = @pinned,
                                 unpinned_at = CASE WHEN @pinned IS NULL THEN @at ELSE unpinned_at END
                WHERE id = @id
                """,
                ("@seq", seq), ("@pinned", pinned ? Format(at) : null), ("@at", Format(at)), ("@id", id.ToString()));
            var dto = await GetCoreAsync(conn, tx, id);
            await tx.CommitAsync();
            return dto;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Deletes every unpinned clip, or only those not copied or unpinned since <paramref name="olderThan"/>, with
    /// tombstones, in one transaction. Returns what was deleted so the caller can remove blobs and broadcast.
    /// </summary>
    public async Task<IReadOnlyList<(ClipDto Clip, long Seq)>> DeleteUnpinnedAsync(DateTimeOffset? olderThan)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = db.Open();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

            // Timestamps are all stored in the same fixed-width UTC format, so text comparison is time order.
            var sql = $"SELECT {Columns} FROM clips c WHERE c.pinned_at IS NULL";
            var args = new List<(string, object?)>();
            if (olderThan is { } cutoff)
            {
                sql += " AND c.last_copied_at < @t AND (c.unpinned_at IS NULL OR c.unpinned_at < @t)";
                args.Add(("@t", Format(cutoff)));
            }

            List<ClipDto> doomed;
            await using (var select = Command(conn, tx, sql + " ORDER BY c.seq ASC", args.ToArray()))
                doomed = await ReadClipsAsync(select);

            var result = new List<(ClipDto, long)>(doomed.Count);
            foreach (var clip in doomed)
            {
                await ExecAsync(conn, tx, "DELETE FROM clips WHERE id = @id", ("@id", clip.Id.ToString()));
                var seq = await NextSeqAsync(conn, tx);
                await ExecAsync(conn, tx, "INSERT OR REPLACE INTO tombstones(clip_id, seq) VALUES(@id, @seq)",
                    ("@id", clip.Id.ToString()), ("@seq", seq));
                result.Add((clip, seq));
            }

            await tx.CommitAsync();
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<SyncResponse> SyncAsync(long afterSeq, int limit)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var conn = db.Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(); // consistent snapshot

        await using var clipsCmd = conn.CreateCommand();
        clipsCmd.Transaction = tx;
        clipsCmd.CommandText = $"SELECT {Columns} FROM clips c WHERE c.seq > @after ORDER BY c.seq ASC LIMIT @limit";
        clipsCmd.Parameters.AddWithValue("@after", afterSeq);
        clipsCmd.Parameters.AddWithValue("@limit", limit + 1);
        var clips = await ReadClipsAsync(clipsCmd);

        var hasMore = clips.Count > limit;
        if (hasMore)
            clips.RemoveAt(clips.Count - 1);

        // When the clip list is truncated, only report tombstones up to the last returned seq so the
        // client can continue from that point without missing any.
        await using var tombCmd = conn.CreateCommand();
        tombCmd.Transaction = tx;
        tombCmd.CommandText = "SELECT clip_id FROM tombstones WHERE seq > @after AND seq <= @upTo ORDER BY seq ASC";
        tombCmd.Parameters.AddWithValue("@after", afterSeq);
        tombCmd.Parameters.AddWithValue("@upTo", hasMore ? clips[^1].Seq : long.MaxValue);
        var deleted = new List<Guid>();
        await using (var reader = await tombCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                deleted.Add(Guid.Parse(reader.GetString(0)));
        }

        var latest = await ScalarAsync<long?>(conn, tx, "SELECT value FROM counters WHERE name = 'seq'") ?? 0;
        return new SyncResponse(clips, deleted, latest, hasMore);
    }

    private static async Task<ClipDto?> BumpCoreAsync(SqliteConnection conn, SqliteTransaction tx, Guid id,
        Guid? deviceId, string deviceName, string? devicePlatform, DateTimeOffset at)
    {
        var current = await ScalarAsync<string>(conn, tx, "SELECT last_copied_at FROM clips WHERE id = @id",
            ("@id", id.ToString()));
        if (current is null)
            return null;

        var newest = DateTimeOffset.Parse(current, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (at > newest)
            newest = at;

        var seq = await NextSeqAsync(conn, tx);
        await ExecAsync(conn, tx, """
            UPDATE clips SET seq = @seq, last_copied_at = @at, source_device_id = @dev, source_device_name = @devName,
                             source_device_platform = @devPlatform
            WHERE id = @id
            """,
            ("@seq", seq), ("@at", Format(newest)), ("@dev", deviceId?.ToString()), ("@devName", deviceName),
            ("@devPlatform", devicePlatform), ("@id", id.ToString()));
        return await GetCoreAsync(conn, tx, id);
    }

    private static async Task<ClipDto?> GetCoreAsync(SqliteConnection conn, SqliteTransaction? tx, Guid id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM clips c WHERE c.id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return (await ReadClipsAsync(cmd)).SingleOrDefault();
    }

    private static async Task<List<ClipDto>> ReadClipsAsync(SqliteCommand cmd)
    {
        var list = new List<ClipDto>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ClipDto(
                Guid.Parse(r.GetString(0)),
                r.GetInt64(1),
                KindFromDb(r.GetString(2)),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetInt32(6),
                r.GetInt64(7),
                r.IsDBNull(8) ? null : Guid.Parse(r.GetString(8)),
                r.GetString(9),
                Parse(r.GetString(10)))
            {
                SourceDevicePlatform = r.IsDBNull(11) ? null : r.GetString(11),
                PinnedAt = r.IsDBNull(12) ? null : Parse(r.GetString(12)),
            });
        }

        return list;
    }

    private static Task<long> NextSeqAsync(SqliteConnection conn, SqliteTransaction tx) =>
        ScalarAsync<long>(conn, tx, """
            INSERT INTO counters(name, value) VALUES('seq', 1)
            ON CONFLICT(name) DO UPDATE SET value = value + 1
            RETURNING value
            """)!;

    private static async Task<T?> ScalarAsync<T>(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        var value = await cmd.ExecuteScalarAsync();
        if (value is null or DBNull)
            return default;
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecAsync(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction tx, string sql,
        (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string KindToDb(ClipKind k) => k switch
    {
        ClipKind.Text => "text",
        ClipKind.Link => "link",
        ClipKind.Image => "image",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    private static ClipKind KindFromDb(string s) => s switch
    {
        "text" => ClipKind.Text,
        "link" => ClipKind.Link,
        "image" => ClipKind.Image,
        _ => throw new InvalidOperationException($"Unknown clip kind '{s}'"),
    };

    private static string EncodeCursor(DateTimeOffset at, long seq) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{Format(at)}|{seq}"));

    private static (DateTimeOffset At, long Seq) DecodeCursor(string cursor)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var parts = text.Split('|');
            if (parts.Length != 2)
                throw new FormatException();
            return (Parse(parts[0]), long.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("Invalid cursor.", nameof(cursor), ex);
        }
    }
}
