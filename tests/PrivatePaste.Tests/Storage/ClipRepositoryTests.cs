using Microsoft.Data.Sqlite;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Storage;

public sealed class ClipRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "privatepaste-clips-" + Guid.NewGuid().ToString("N"));
    private readonly ClipRepository _repo;

    public ClipRepositoryTests()
    {
        var db = new PrivatePasteDb(new ServerOptions("key-0123456789abcdef", "pw", _dir, 1024, 1024));
        db.Initialize();
        _repo = new ClipRepository(db);
    }

    private Task<UpsertResult> Upsert(string text, string device = "dev", DateTimeOffset? at = null,
        ClipKind kind = ClipKind.Text, string? hash = null, string? platform = "windows") =>
        _repo.UpsertAsync(new NewClip(kind, kind == ClipKind.Image ? null : text, hash ?? "h:" + text, null, null,
            text.Length, null, device, platform, at ?? T0));

    [Fact]
    public async Task Same_hash_bumps_not_duplicates()
    {
        var a = await Upsert("x", device: "PC-work", at: T0);
        var b = await Upsert("x", device: "Phone", at: T0.AddMinutes(1));
        Assert.True(a.Created);
        Assert.False(b.Created);
        Assert.Equal(a.Clip.Id, b.Clip.Id);
        Assert.True(b.Clip.Seq > a.Clip.Seq);
        Assert.Equal("Phone", b.Clip.SourceDeviceName);
        Assert.Single((await _repo.ListAsync(null, null, 50)).Items);
    }

    [Fact]
    public async Task Platform_is_stored_and_a_dedup_bump_takes_the_bumping_devices()
    {
        var a = await Upsert("x", device: "PC-work", platform: "windows");
        Assert.Equal("windows", a.Clip.SourceDevicePlatform);

        var b = await Upsert("x", device: "Phone", at: T0.AddMinutes(1), platform: "android");
        Assert.Equal("android", b.Clip.SourceDevicePlatform);
        Assert.Equal("android", (await _repo.GetAsync(a.Clip.Id))!.SourceDevicePlatform);

        var c = await Upsert("y", platform: null);
        Assert.Null(c.Clip.SourceDevicePlatform);
    }

    [Fact]
    public async Task Bump_never_moves_time_backwards()
    {
        await Upsert("x", at: T0.AddHours(1));
        var b = await Upsert("x", at: T0);
        Assert.Equal(T0.AddHours(1), b.Clip.LastCopiedAt);
    }

    [Fact]
    public async Task Bump_by_id_updates_and_unknown_id_returns_null()
    {
        var a = await Upsert("x", at: T0);
        var bumped = await _repo.BumpAsync(a.Clip.Id, null, "Tablet", "linux", null, T0.AddMinutes(5));
        Assert.NotNull(bumped);
        Assert.Equal("Tablet", bumped!.SourceDeviceName);
        Assert.Equal("linux", bumped.SourceDevicePlatform);
        Assert.Equal(T0.AddMinutes(5), bumped.LastCopiedAt);
        Assert.True(bumped.Seq > a.Clip.Seq);
        Assert.Null(await _repo.BumpAsync(Guid.NewGuid(), null, "x", null, null, T0));
    }

    [Fact]
    public async Task Get_returns_clip_or_null()
    {
        var a = await Upsert("x");
        Assert.Equal(a.Clip, await _repo.GetAsync(a.Clip.Id));
        Assert.Null(await _repo.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task List_orders_by_last_copied_desc()
    {
        var a = await Upsert("a", at: T0);
        var b = await Upsert("b", at: T0.AddMinutes(2));
        var c = await Upsert("c", at: T0.AddMinutes(1));
        var page = await _repo.ListAsync(null, null, 50);
        Assert.Equal([b.Clip.Id, c.Clip.Id, a.Clip.Id], page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Paging_with_cursor_covers_all_without_overlap()
    {
        for (var i = 0; i < 7; i++)
            await Upsert("clip" + i, at: T0.AddMinutes(i % 3)); // ties on time exercise the seq tiebreak

        var p1 = await _repo.ListAsync(null, null, 3);
        var p2 = await _repo.ListAsync(null, p1.NextCursor, 3);
        var p3 = await _repo.ListAsync(null, p2.NextCursor, 3);

        Assert.Equal(3, p1.Items.Count);
        Assert.Equal(3, p2.Items.Count);
        Assert.Single(p3.Items);
        Assert.NotNull(p1.NextCursor);
        Assert.NotNull(p2.NextCursor);
        Assert.Null(p3.NextCursor);
        var ids = p1.Items.Concat(p2.Items).Concat(p3.Items).Select(i => i.Id).ToList();
        Assert.Equal(7, ids.Distinct().Count());
    }

    [Fact]
    public async Task Paging_exact_multiple_has_null_cursor_on_last_page()
    {
        for (var i = 0; i < 4; i++)
            await Upsert("c" + i, at: T0.AddMinutes(i));
        var p1 = await _repo.ListAsync(null, null, 2);
        var p2 = await _repo.ListAsync(null, p1.NextCursor, 2);
        Assert.Equal(2, p2.Items.Count);
        Assert.Null(p2.NextCursor);
    }

    [Fact]
    public async Task List_limit_is_clamped()
    {
        await Upsert("a");
        await Upsert("b");
        Assert.Single((await _repo.ListAsync(null, null, 0)).Items);
        Assert.Single((await _repo.ListAsync(null, null, -5)).Items);
    }

    [Fact]
    public async Task Invalid_cursor_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.ListAsync(null, "!!notacursor!!", 10));
    }

    [Fact]
    public async Task Search_trigram_substring()
    {
        var hit = await Upsert("docker compose up -d");
        await Upsert("something else entirely");
        var page = await _repo.ListAsync("compose", null, 50);
        Assert.Equal(hit.Clip.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Search_non_ascii()
    {
        var a = await Upsert("kupiti šećer i mlijeko");
        var b = await Upsert("deploy 🚀 done");
        await Upsert("unrelated");
        Assert.Equal(a.Clip.Id, Assert.Single((await _repo.ListAsync("šećer", null, 50)).Items).Id);
        Assert.Equal(b.Clip.Id, Assert.Single((await _repo.ListAsync("🚀", null, 50)).Items).Id);
    }

    [Fact]
    public async Task Search_short_query_uses_like()
    {
        var hit = await Upsert("xaby");
        await Upsert("nothing");
        var pct = await Upsert("100% sure");
        Assert.Equal(hit.Clip.Id, Assert.Single((await _repo.ListAsync("ab", null, 50)).Items).Id);
        Assert.Equal(pct.Clip.Id, Assert.Single((await _repo.ListAsync("%", null, 50)).Items).Id);
    }

    [Fact]
    public async Task Search_short_query_underscore_is_literal()
    {
        var hit = await Upsert("snake_case");
        await Upsert("snakeXcase");
        Assert.Equal(hit.Clip.Id, Assert.Single((await _repo.ListAsync("_", null, 50)).Items).Id);
    }

    [Theory]
    [InlineData("\"unclosed")]
    [InlineData("a OR b")]
    [InlineData("-x*")]
    [InlineData("(foo")]
    public async Task Search_with_fts_syntax_is_literal(string q)
    {
        var hit = await Upsert("prefix " + q + " suffix");
        await Upsert("a b x foo unclosed");
        var page = await _repo.ListAsync(q, null, 50);
        Assert.Equal(hit.Clip.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Search_case_insensitive_ascii()
    {
        var hit = await Upsert("docker");
        Assert.Equal(hit.Clip.Id, Assert.Single((await _repo.ListAsync("Docker", null, 50)).Items).Id);
        Assert.Equal(hit.Clip.Id, Assert.Single((await _repo.ListAsync("DO", null, 50)).Items).Id);
    }

    [Fact]
    public async Task Image_clips_never_match_search()
    {
        await Upsert("img-bytes", kind: ClipKind.Image, hash: "imghash");
        Assert.Empty((await _repo.ListAsync("img", null, 50)).Items);
        Assert.Empty((await _repo.ListAsync("im", null, 50)).Items);
        Assert.Single((await _repo.ListAsync(null, null, 50)).Items);
    }

    [Fact]
    public async Task Search_pages_with_cursor()
    {
        for (var i = 0; i < 5; i++)
            await Upsert("needle " + i, at: T0.AddMinutes(i));
        var p1 = await _repo.ListAsync("needle", null, 3);
        var p2 = await _repo.ListAsync("needle", p1.NextCursor, 3);
        Assert.Equal(3, p1.Items.Count);
        Assert.Equal(2, p2.Items.Count);
        Assert.Null(p2.NextCursor);
    }

    [Fact]
    public async Task Delete_creates_tombstone_seen_by_sync()
    {
        var a = await Upsert("a");
        await Upsert("b");
        var s = (await _repo.SyncAsync(0, 500)).LatestSeq;

        var seq = await _repo.DeleteAsync(a.Clip.Id);

        Assert.NotNull(seq);
        Assert.True(seq > s);
        Assert.Null(await _repo.GetAsync(a.Clip.Id));
        var sync = await _repo.SyncAsync(s, 500);
        Assert.Equal([a.Clip.Id], sync.DeletedIds);
        Assert.Empty(sync.Clips);
        Assert.Equal(seq, sync.LatestSeq);
        Assert.DoesNotContain(a.Clip.Id, (await _repo.ListAsync("a", null, 50)).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Delete_unknown_returns_null()
    {
        Assert.Null(await _repo.DeleteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Reinserting_deleted_content_creates_new_clip()
    {
        var a = await Upsert("again");
        await _repo.DeleteAsync(a.Clip.Id);
        var b = await Upsert("again");
        Assert.True(b.Created);
        Assert.NotEqual(a.Clip.Id, b.Clip.Id);
    }

    [Fact]
    public async Task Sync_returns_changes_after_seq_in_seq_order()
    {
        var a = await Upsert("a", at: T0);
        var b = await Upsert("b", at: T0);
        var s = (await _repo.SyncAsync(0, 500)).LatestSeq;
        await Upsert("a", at: T0.AddMinutes(1)); // bump a
        var c = await Upsert("c", at: T0);

        var sync = await _repo.SyncAsync(s, 500);

        Assert.Equal([a.Clip.Id, c.Clip.Id], sync.Clips.Select(x => x.Id));
        Assert.False(sync.HasMore);
        Assert.Empty(sync.DeletedIds);
        Assert.Equal(c.Clip.Seq, sync.LatestSeq);
        Assert.DoesNotContain(b.Clip.Id, sync.Clips.Select(x => x.Id));
    }

    [Fact]
    public async Task Sync_has_more_when_limited()
    {
        for (var i = 0; i < 5; i++)
            await Upsert("c" + i);
        var sync = await _repo.SyncAsync(0, 2);
        Assert.Equal(2, sync.Clips.Count);
        Assert.True(sync.HasMore);
        Assert.Equal(5, sync.LatestSeq);

        var rest = await _repo.SyncAsync(sync.Clips[^1].Seq, 500);
        Assert.Equal(3, rest.Clips.Count);
        Assert.False(rest.HasMore);
    }

    [Fact]
    public async Task Sync_on_empty_db_has_zero_latest_seq()
    {
        var sync = await _repo.SyncAsync(0, 10);
        Assert.Empty(sync.Clips);
        Assert.Equal(0, sync.LatestSeq);
        Assert.False(sync.HasMore);
    }

    [Fact]
    public async Task Concurrent_upserts_get_unique_seqs()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Upsert("c" + i)));
        Assert.Equal(20, results.Select(r => r.Clip.Seq).Distinct().Count());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
