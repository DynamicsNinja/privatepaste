using System.Text;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Client;

public class HistoryCacheTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-cache-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "history.enc");
    private static byte[] NewKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    public HistoryCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ClipDto Clip(long seq, string? text, int minutes, Guid? id = null, ClipKind kind = ClipKind.Text) =>
        new(id ?? Guid.NewGuid(), seq, kind, text, "hash" + seq, null, null, text?.Length ?? 0, null, "dev", T0.AddMinutes(minutes));

    [Fact]
    public void Orders_newest_first()
    {
        var c = new HistoryCache(FilePath, NewKey());
        c.Upsert(Clip(1, "a", 1));
        c.Upsert(Clip(3, "c", 3));
        c.Upsert(Clip(2, "b", 2));
        Assert.Equal(new[] { "c", "b", "a" }, c.Items.Select(i => i.Clip.Text));
        Assert.Equal(3, c.HighestSeq);
    }

    [Fact]
    public void Upsert_same_id_replaces_and_reorders()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var id = Guid.NewGuid();
        c.Upsert(Clip(1, "old", 1, id));
        c.Upsert(Clip(2, "other", 2));
        c.Upsert(Clip(3, "old", 5, id));
        Assert.Equal(2, c.Items.Count);
        Assert.Equal(id, c.Items[0].Clip.Id);
        Assert.Equal(3, c.HighestSeq);
    }

    [Fact]
    public void Trims_to_capacity()
    {
        var c = new HistoryCache(FilePath, NewKey(), capacity: 3);
        for (var i = 1; i <= 5; i++) c.Upsert(Clip(i, "t" + i, i));
        Assert.Equal(new[] { "t5", "t4", "t3" }, c.Items.Select(i => i.Clip.Text));
        Assert.Equal(5, c.HighestSeq);
    }

    [Fact]
    public void Remove_deletes_and_raises_highest_seq()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var id = Guid.NewGuid();
        c.Upsert(Clip(1, "x", 1, id));
        c.Remove(id, 9);
        Assert.Empty(c.Items);
        Assert.Equal(9, c.HighestSeq);
        c.Remove(Guid.NewGuid(), 4);
        Assert.Equal(9, c.HighestSeq);
    }

    [Fact]
    public void Search_is_case_insensitive_and_skips_images()
    {
        var c = new HistoryCache(FilePath, NewKey());
        c.Upsert(Clip(1, "docker compose", 1));
        c.Upsert(Clip(2, "kupiti šećer", 2));
        c.Upsert(Clip(3, null, 3, kind: ClipKind.Image), new byte[] { 1, 2, 3 });
        Assert.Equal("docker compose", Assert.Single(c.Search("DOCKER")).Clip.Text);
        Assert.Equal("kupiti šećer", Assert.Single(c.Search("šećer")).Clip.Text);
        Assert.Empty(c.Search("image"));
        Assert.Equal(3, c.Search("").Count);
    }

    [Fact]
    public void Persists_encrypted()
    {
        var key = NewKey();
        var c = new HistoryCache(FilePath, key);
        var clip = Clip(7, "supersecret", 4);
        c.Upsert(clip, new byte[] { 9, 8, 7 });
        c.MarkApplied(clip);
        c.Save();

        Assert.DoesNotContain("supersecret", Encoding.UTF8.GetString(File.ReadAllBytes(FilePath)));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        var l = HistoryCache.Load(FilePath, key);
        var item = Assert.Single(l.Items);
        Assert.Equal(clip, item.Clip);
        Assert.Equal(new byte[] { 9, 8, 7 }, item.ThumbnailPng);
        Assert.Equal(7, l.HighestSeq);
        Assert.Equal(clip.LastCopiedAt, l.LastAppliedAt);
    }

    [Fact]
    public void Wrong_key_or_corrupt_loads_empty()
    {
        var key = NewKey();
        var c = new HistoryCache(FilePath, key);
        c.Upsert(Clip(1, "a", 1));
        c.Save();

        Assert.Empty(HistoryCache.Load(FilePath, NewKey()).Items);

        var bytes = File.ReadAllBytes(FilePath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(FilePath, bytes);
        var corrupt = HistoryCache.Load(FilePath, key);
        Assert.Empty(corrupt.Items);
        Assert.Equal(0, corrupt.HighestSeq);

        File.WriteAllBytes(FilePath, new byte[5]);
        Assert.Empty(HistoryCache.Load(FilePath, key).Items);
        Assert.Empty(HistoryCache.Load(Path.Combine(_dir, "missing.enc"), key).Items);
    }

    [Fact]
    public void Clear_deletes_file_and_state()
    {
        var c = new HistoryCache(FilePath, NewKey());
        c.Upsert(Clip(1, "a", 1));
        c.Save();
        c.Clear();
        Assert.False(File.Exists(FilePath));
        Assert.Empty(c.Items);
        Assert.Equal(0, c.HighestSeq);
    }

    [Fact]
    public void Stale_seq_upsert_is_ignored()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var id = Guid.NewGuid();
        c.Upsert(Clip(5, "new", 5, id));
        c.Upsert(Clip(3, "old", 3, id));
        var item = Assert.Single(c.Items);
        Assert.Equal(5, item.Clip.Seq);
        Assert.Equal(T0.AddMinutes(5), item.Clip.LastCopiedAt);
        Assert.Equal(5, c.HighestSeq);
    }

    [Fact]
    public void Late_upsert_after_remove_does_not_resurrect()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var id = Guid.NewGuid();
        c.Remove(id, 8);
        c.Upsert(Clip(6, "ghost", 6, id));
        Assert.Empty(c.Items);
        c.Upsert(Clip(8, "ghost", 6, id));
        Assert.Empty(c.Items);
        Assert.Equal(8, c.HighestSeq);
    }

    [Fact]
    public void Newer_upsert_after_remove_is_accepted()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var id = Guid.NewGuid();
        c.Remove(id, 8);
        c.Upsert(Clip(9, "back", 9, id));
        Assert.Equal("back", Assert.Single(c.Items).Clip.Text);
    }

    [Fact]
    public void Tombstones_survive_save_load()
    {
        var key = NewKey();
        var c = new HistoryCache(FilePath, key);
        var id = Guid.NewGuid();
        c.Remove(id, 8);
        c.Save();
        var l = HistoryCache.Load(FilePath, key);
        l.Upsert(Clip(6, "ghost", 6, id));
        Assert.Empty(l.Items);
    }

    [Fact]
    public void Tombstones_are_bounded()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var first = Guid.NewGuid();
        c.Remove(first, 1);
        for (var i = 2; i <= 1001; i++) c.Remove(Guid.NewGuid(), i);
        c.Upsert(Clip(1, "x", 1, first));
        Assert.Single(c.Items);
    }

    [Fact]
    public void Bump_without_thumbnail_keeps_existing_thumbnail()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var id = Guid.NewGuid();
        var thumb = new byte[] { 1, 2, 3 };
        var a = Clip(1, null, 1, id, ClipKind.Image);
        c.Upsert(a, thumb);
        c.Upsert(a with { Seq = 2, LastCopiedAt = T0.AddMinutes(9) });
        Assert.Equal(thumb, Assert.Single(c.Items).ThumbnailPng);
        c.Upsert(a with { Seq = 3, Hash = "different" });
        Assert.Null(Assert.Single(c.Items).ThumbnailPng);
    }

    [Fact]
    public void Mark_applied_never_moves_backwards()
    {
        var c = new HistoryCache(FilePath, NewKey());
        c.MarkApplied(Clip(2, "b", 5));
        c.MarkApplied(Clip(1, "a", 2));
        Assert.Equal(T0.AddMinutes(5), c.LastAppliedAt);
    }

    [Fact]
    public void Changed_is_raised_for_real_changes_only()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var changes = 0;
        c.Changed += (_, _) => changes++;
        var id = Guid.NewGuid();

        c.Upsert(Clip(2, "a", 1, id));
        Assert.Equal(1, changes);
        c.Upsert(Clip(1, "a", 1, id));   // stale: ignored
        Assert.Equal(1, changes);
        c.Remove(id, 3);
        Assert.Equal(2, changes);
        c.Upsert(Clip(3, "a", 1, id));   // tombstoned: ignored
        Assert.Equal(2, changes);
        c.Clear();
        Assert.Equal(3, changes);
    }
    [Fact]
    public async Task Concurrent_saves_never_collide()
    {
        var key = NewKey();
        var c = new HistoryCache(FilePath, key);
        c.Upsert(Clip(1, "a", 1));
        using var start = new Barrier(2);
        void SaveMany()
        {
            start.SignalAndWait();
            for (var i = 0; i < 300; i++) c.Save();
        }
        await Task.WhenAll(Task.Factory.StartNew(SaveMany, TaskCreationOptions.LongRunning),
            Task.Factory.StartNew(SaveMany, TaskCreationOptions.LongRunning));
        Assert.Single(HistoryCache.Load(FilePath, key).Items);
    }

    [Fact]
    public void Upsert_and_remove_report_whether_anything_changed()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var id = Guid.NewGuid();
        Assert.True(c.Upsert(Clip(2, "a", 1, id)));
        Assert.False(c.Upsert(Clip(2, "a", 1, id)));   // same seq: nothing new
        Assert.False(c.Upsert(Clip(1, "a", 1, id)));   // older
        Assert.True(c.Remove(id, 3));
        Assert.False(c.Remove(id, 4));                 // nothing held
        Assert.False(c.Upsert(Clip(3, "a", 1, id)));   // already deleted
    }

    [Fact]
    public void Own_upload_responses_do_not_move_the_sync_position()
    {
        var key = NewKey();
        var c = new HistoryCache(FilePath, key);
        c.Upsert(Clip(3, "synced", 1));
        Assert.True(c.Upsert(Clip(9, "uploaded here", 2), fromSync: false));
        Assert.Equal(3, c.HighestSeq);
        c.Save();
        Assert.Equal(3, HistoryCache.Load(FilePath, key).HighestSeq);
    }

    [Fact]
    public void Set_thumbnail_only_touches_a_held_clip_with_the_same_content()
    {
        var c = new HistoryCache(FilePath, NewKey());
        var clip = Clip(1, null, 1, kind: ClipKind.Image);
        c.Upsert(clip);
        Assert.False(c.SetThumbnail(clip.Id, "other hash", [1]));
        Assert.False(c.SetThumbnail(Guid.NewGuid(), clip.Hash, [1]));
        Assert.True(c.SetThumbnail(clip.Id, clip.Hash, [1, 2]));
        Assert.Equal(new byte[] { 1, 2 }, Assert.Single(c.Items).ThumbnailPng);
        c.Remove(clip.Id, 2);
        Assert.False(c.SetThumbnail(clip.Id, clip.Hash, [3]));
        Assert.Empty(c.Items);
    }
}
