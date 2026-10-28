using System.Security.Cryptography;
using System.Text.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Cache;

public sealed record CachedClip(ClipDto Clip, byte[]? ThumbnailPng);

/// <summary>
/// Local, encrypted history of recent clips. File layout matches the server's BlobStore:
/// nonce(12) || tag(16) || ciphertext (AES-256-GCM). Thread-safe; call <see cref="Save"/> after a batch of changes.
/// </summary>
public sealed class HistoryCache(string path, byte[] key, int capacity = 500)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly object _gate = new();
    private readonly List<CachedClip> _items = [];
    private const int MaxTombstones = 1000;
    private readonly Dictionary<Guid, long> _tombstones = [];
    private long _highestSeq;
    private DateTimeOffset? _lastAppliedAt;

    public long HighestSeq { get { lock (_gate) return _highestSeq; } }

    public DateTimeOffset? LastAppliedAt { get { lock (_gate) return _lastAppliedAt; } }

    /// <summary>Snapshot, newest first (LastCopiedAt desc, then Seq desc).</summary>
    public IReadOnlyList<CachedClip> Items { get { lock (_gate) return _items.ToArray(); } }

    public void Upsert(ClipDto clip, byte[]? thumbnailPng = null)
    {
        lock (_gate)
        {
            if (clip.Seq > _highestSeq) _highestSeq = clip.Seq;
            // Broadcasts can arrive out of order: ignore anything older than what we hold or already deleted.
            if (_tombstones.TryGetValue(clip.Id, out var dead) && dead >= clip.Seq) return;
            var existing = _items.Find(i => i.Clip.Id == clip.Id);
            if (existing is not null && clip.Seq <= existing.Clip.Seq) return;
            if (thumbnailPng is null && existing is not null && existing.Clip.Hash == clip.Hash)
                thumbnailPng = existing.ThumbnailPng;
            _tombstones.Remove(clip.Id);
            _items.RemoveAll(i => i.Clip.Id == clip.Id);
            _items.Add(new CachedClip(clip, thumbnailPng));
            _items.Sort(Compare);
            if (_items.Count > capacity) _items.RemoveRange(capacity, _items.Count - capacity);
        }
    }

    public void Remove(Guid id, long seq)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Clip.Id == id);
            if (seq > _highestSeq) _highestSeq = seq;
            if (!_tombstones.TryGetValue(id, out var prev) || seq > prev) _tombstones[id] = seq;
            TrimTombstones(_tombstones);
        }
    }

    public void MarkApplied(ClipDto clip)
    {
        lock (_gate)
            if (_lastAppliedAt is null || clip.LastCopiedAt > _lastAppliedAt) _lastAppliedAt = clip.LastCopiedAt;
    }

    public IReadOnlyList<CachedClip> Search(string query)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(query)) return _items.ToArray();
            return _items.Where(i => i.Clip.Text is { } t && t.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

    public void Save()
    {
        byte[] plaintext;
        lock (_gate)
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new Snapshot(_highestSeq, _lastAppliedAt, _items.ToList(), new Dictionary<Guid, long>(_tombstones)), ProtocolJson.Options);

        var file = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = file.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plaintext, file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize));

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(file);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Missing, corrupt or wrong-key files yield an empty cache; never throws.</summary>
    public static HistoryCache Load(string path, byte[] key, int capacity = 500)
    {
        var cache = new HistoryCache(path, key, capacity);
        try
        {
            if (!File.Exists(path)) return cache;
            var file = File.ReadAllBytes(path);
            if (file.Length < NonceSize + TagSize) return cache;
            var plaintext = new byte[file.Length - NonceSize - TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(file.AsSpan(0, NonceSize), file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize), plaintext);
            var snap = JsonSerializer.Deserialize<Snapshot>(plaintext, ProtocolJson.Options);
            if (snap?.Items is null) return cache;
            var loaded = snap.Items.Where(i => i?.Clip is not null).ToList();
            loaded.Sort(Compare);
            if (loaded.Count > capacity) loaded.RemoveRange(capacity, loaded.Count - capacity);
            cache._items.AddRange(loaded);
            cache._highestSeq = Math.Max(snap.HighestSeq, loaded.Select(i => i.Clip.Seq).DefaultIfEmpty(0).Max());
            cache._lastAppliedAt = snap.LastAppliedAt;
            if (snap.Tombstones is not null)
            {
                foreach (var (id, seq) in snap.Tombstones) cache._tombstones[id] = seq;
                TrimTombstones(cache._tombstones);
            }
        }
        catch (Exception)
        {
            cache._items.Clear();
            cache._highestSeq = 0;
            cache._lastAppliedAt = null;
            cache._tombstones.Clear();
        }
        return cache;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _highestSeq = 0;
            _lastAppliedAt = null;
            _tombstones.Clear();
        }
        try { File.Delete(path); } catch (IOException) { }
        try { File.Delete(path + ".tmp"); } catch (IOException) { }
    }

    private static int Compare(CachedClip a, CachedClip b)
    {
        var c = b.Clip.LastCopiedAt.CompareTo(a.Clip.LastCopiedAt);
        return c != 0 ? c : b.Clip.Seq.CompareTo(a.Clip.Seq);
    }

    private static void TrimTombstones(Dictionary<Guid, long> t)
    {
        if (t.Count <= MaxTombstones) return;
        foreach (var k in t.OrderBy(kv => kv.Value).Take(t.Count - MaxTombstones).Select(kv => kv.Key).ToList())
            t.Remove(k);
    }

    private sealed record Snapshot(long HighestSeq, DateTimeOffset? LastAppliedAt, List<CachedClip> Items,
        Dictionary<Guid, long>? Tombstones = null);
}
