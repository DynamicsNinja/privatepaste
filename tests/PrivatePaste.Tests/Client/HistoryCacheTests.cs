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
}
