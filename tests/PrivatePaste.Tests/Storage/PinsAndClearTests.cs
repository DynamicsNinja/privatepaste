using System.Globalization;
using Microsoft.Data.Sqlite;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Storage;

public sealed class PinsAndClearTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "privatepaste-pins-" + Guid.NewGuid().ToString("N"));
    private readonly PrivatePasteDb _db;
    private readonly ClipRepository _repo;

    public PinsAndClearTests()
    {
        _db = new PrivatePasteDb(new ServerOptions("key-0123456789abcdef", "pw", _dir, 1024, 1024));
        _db.Initialize();
        _repo = new ClipRepository(_db);
    }

    private Task<UpsertResult> Upsert(string text, DateTimeOffset? at = null) =>
        _repo.UpsertAsync(new NewClip(ClipKind.Text, text, "h:" + text, null, null, text.Length, null, "dev",
            "windows", at ?? T0));

    [Fact]
    public async Task Pin_sets_pinnedAt_and_a_new_seq_but_keeps_lastCopiedAt()
    {
        var clip = (await Upsert("a")).Clip;
        Assert.Null(clip.PinnedAt);

        var pinned = await _repo.SetPinnedAsync(clip.Id, true, T0.AddHours(1));

        Assert.NotNull(pinned);
        Assert.Equal(T0.AddHours(1), pinned.PinnedAt);
        Assert.True(pinned.Seq > clip.Seq);
        Assert.Equal(clip.LastCopiedAt, pinned.LastCopiedAt);
    }

    [Fact]
    public async Task Pin_of_a_missing_clip_is_null()
    {
        Assert.Null(await _repo.SetPinnedAsync(Guid.NewGuid(), true, T0));
    }

    [Fact]
    public async Task Pinning_twice_changes_nothing()
    {
        var clip = (await Upsert("a")).Clip;
        var first = await _repo.SetPinnedAsync(clip.Id, true, T0.AddHours(1));
        var second = await _repo.SetPinnedAsync(clip.Id, true, T0.AddHours(2));

        Assert.Equal(first!.Seq, second!.Seq);
        Assert.Equal(first.PinnedAt, second.PinnedAt);

        // Unpinning an unpinned clip is a no-op too.
        var other = (await Upsert("b")).Clip;
        var unpinned = await _repo.SetPinnedAsync(other.Id, false, T0.AddHours(3));
        Assert.Equal(other.Seq, unpinned!.Seq);
    }

    [Fact]
    public async Task Unpin_clears_pinnedAt_and_records_unpinnedAt()
    {
        var clip = (await Upsert("a")).Clip;
        var pinned = await _repo.SetPinnedAsync(clip.Id, true, T0.AddHours(1));
        var unpinned = await _repo.SetPinnedAsync(clip.Id, false, T0.AddHours(2));

        Assert.Null(unpinned!.PinnedAt);
        Assert.True(unpinned.Seq > pinned!.Seq);
        Assert.Equal(T0.AddHours(2), ReadUnpinnedAt(clip.Id));
    }

    [Fact]
    public async Task ListPinned_is_newest_pinned_first_and_searchable()
    {
        var a = (await Upsert("alpha clip", T0)).Clip;
        var b = (await Upsert("bravo clip", T0.AddMinutes(1))).Clip;
        var c = (await Upsert("charlie clip", T0.AddMinutes(2))).Clip;
        await Upsert("delta clip never pinned", T0.AddMinutes(3));
        await _repo.SetPinnedAsync(a.Id, true, T0.AddHours(3));
        await _repo.SetPinnedAsync(b.Id, true, T0.AddHours(1));
        await _repo.SetPinnedAsync(c.Id, true, T0.AddHours(2));

        var all = await _repo.ListPinnedAsync(null, null, 50);
        Assert.Equal([a.Id, c.Id, b.Id], all.Items.Select(i => i.Id));

        var page1 = await _repo.ListPinnedAsync(null, null, 2);
        Assert.Equal([a.Id, c.Id], page1.Items.Select(i => i.Id));
        Assert.NotNull(page1.NextCursor);
        var page2 = await _repo.ListPinnedAsync(null, page1.NextCursor, 2);
        Assert.Equal([b.Id], page2.Items.Select(i => i.Id));
        Assert.Null(page2.NextCursor);

        var found = await _repo.ListPinnedAsync("bravo", null, 50);
        Assert.Equal([b.Id], found.Items.Select(i => i.Id));
        Assert.Empty((await _repo.ListPinnedAsync("delta", null, 50)).Items);
    }

    [Fact]
    public async Task DeleteUnpinned_keeps_pins_and_tombstones_the_rest()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 1000; i++)
            ids.Add((await Upsert("clip " + i, T0.AddSeconds(i))).Clip.Id);
        var pinned = ids.Take(3).ToList();
        foreach (var id in pinned)
            await _repo.SetPinnedAsync(id, true, T0.AddDays(1));

        var deleted = await _repo.DeleteUnpinnedAsync(null);

        Assert.Equal(997, deleted.Count);
        Assert.Equal(997, deleted.Select(d => d.Seq).Distinct().Count());
        Assert.DoesNotContain(deleted, d => pinned.Contains(d.Clip.Id));
        var sync = await _repo.SyncAsync(0, 500);
        Assert.Equal(3, sync.Clips.Count);
        Assert.Equal(pinned.OrderBy(x => x), sync.Clips.Select(c => c.Id).OrderBy(x => x));
        Assert.Equal(997, sync.DeletedIds.Count);
        Assert.Empty((await _repo.ListAsync("clip 500", null, 50)).Items); // gone from the FTS index too
    }

    [Fact]
    public async Task DeleteUnpinned_olderThan_uses_the_later_of_copy_and_unpin()
    {
        var old = (await Upsert("old", T0)).Clip;
        var recent = (await Upsert("recent", T0.AddDays(10))).Clip;
        var oldButJustUnpinned = (await Upsert("unpinned", T0)).Clip;
        await _repo.SetPinnedAsync(oldButJustUnpinned.Id, true, T0.AddDays(1));
        await _repo.SetPinnedAsync(oldButJustUnpinned.Id, false, T0.AddDays(11));
        var oldPinned = (await Upsert("pinned", T0)).Clip;
        await _repo.SetPinnedAsync(oldPinned.Id, true, T0.AddDays(1));

        var deleted = await _repo.DeleteUnpinnedAsync(T0.AddDays(5));

        Assert.Equal([old.Id], deleted.Select(d => d.Clip.Id));
        Assert.NotNull(await _repo.GetAsync(recent.Id));
        Assert.NotNull(await _repo.GetAsync(oldButJustUnpinned.Id));
        Assert.NotNull(await _repo.GetAsync(oldPinned.Id));
    }

    [Fact]
    public void Migration_adds_columns_to_an_existing_database_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "privatepaste-pinmig-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var options = new ServerOptions("key-0123456789abcdef", "pw", dir, 1024, 1024);
            var db = new PrivatePasteDb(options);
            using (var old = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = db.DbPath, Password = options.DbKey, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
            {
                old.Open();
                using var cmd = old.CreateCommand();
                // The clips table as the current schema creates it, minus the pin columns.
                cmd.CommandText = """
                    CREATE TABLE clips(id TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, text TEXT,
                      hash TEXT NOT NULL UNIQUE, width INTEGER, height INTEGER, size_bytes INTEGER NOT NULL,
                      source_device_id TEXT, source_device_name TEXT NOT NULL, created_at TEXT NOT NULL,
                      last_copied_at TEXT NOT NULL, source_device_platform TEXT);
                    INSERT INTO clips VALUES('c1', 1, 'text', 'a', 'h1', NULL, NULL, 1, NULL, 'Web', 'x', 'x', 'web');
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            db.Initialize();
            db.Initialize();

            using var conn = db.Open();
            using var check = conn.CreateCommand();
            check.CommandText = """
                SELECT (SELECT count(*) FROM pragma_table_info('clips') WHERE name IN ('pinned_at', 'unpinned_at')),
                       (SELECT count(*) FROM sqlite_master WHERE name IN ('ix_clips_pinned', 'settings')),
                       (SELECT count(*) FROM clips WHERE pinned_at IS NULL)
                """;
            using var r = check.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(2, r.GetInt32(0));
            Assert.Equal(2, r.GetInt32(1));
            Assert.Equal(1, r.GetInt32(2));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    private DateTimeOffset ReadUnpinnedAt(Guid id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT unpinned_at FROM clips WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return DateTimeOffset.Parse((string)cmd.ExecuteScalar()!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }
}
