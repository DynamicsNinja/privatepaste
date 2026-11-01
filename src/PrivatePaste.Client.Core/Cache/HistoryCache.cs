using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Cache;

public sealed record CachedClip(ClipDto Clip, byte[]? ThumbnailPng);

/// <summary>
/// Local, encrypted history of recent clips, as decoded by <see cref="Vault.ClipCodec"/>: they hold plaintext, which is
/// why the file is encrypted with a key of its own. File layout matches the server's BlobStore:
/// nonce(12) || tag(16) || ciphertext (AES-256-GCM). Thread-safe; call <see cref="Save"/> after a batch of changes.
/// </summary>
public sealed class HistoryCache(string path, byte[] key, int capacity = 500)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly object _gate = new();
    /// <summary>Serializes <see cref="Save"/> (all writers share one .tmp file); never taken while holding <see cref="_gate"/>.</summary>
    private readonly object _saveLock = new();
    private readonly List<CachedClip> _items = [];
    private const int MaxTombstones = 1000;
    private readonly Dictionary<Guid, long> _tombstones = [];
    private long _highestSeq;
    private DateTimeOffset? _lastAppliedAt;
    private string? _keyId;

    /// <summary>
    /// The visible items changed (upsert, removal of a held item, clear). Raised synchronously on the mutating thread,
    /// outside the lock, once per call: handlers must be quick, must not throw, and should coalesce/marshal themselves.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// How far the sync stream (catch-up and live pushes) has been read. Local uploads do not move it: a clip uploaded
    /// here while the socket is down must not make the next catch-up skip other devices' clips with lower seqs.
    /// </summary>
    public long HighestSeq { get { lock (_gate) return _highestSeq; } }

    public DateTimeOffset? LastAppliedAt { get { lock (_gate) return _lastAppliedAt; } }

    /// <summary>
    /// The id of the vault key the held clips were decoded with; null for a cache from before encryption (or a new one).
    /// A cache whose key id differs from the server's vault belongs to a history that a reset wiped.
    /// </summary>
    public string? KeyId
    {
        get { lock (_gate) return _keyId; }
        set { lock (_gate) _keyId = value; }
    }

    /// <summary>Snapshot, newest first (LastCopiedAt desc, then Seq desc).</summary>
    public IReadOnlyList<CachedClip> Items { get { lock (_gate) return _items.ToArray(); } }

    /// <summary>Snapshot of the pinned clips, newest pinned first (PinnedAt desc, then Seq desc).</summary>
    public IReadOnlyList<CachedClip> Pinned
    {
        get
        {
            lock (_gate)
                return _items.Where(i => i.Clip.PinnedAt is not null)
                    .OrderByDescending(i => i.Clip.PinnedAt).ThenByDescending(i => i.Clip.Seq).ToArray();
        }
    }

    /// <summary>
    /// Adds or replaces a clip. False when nothing changed (not newer than what is held, or already deleted).
    /// <paramref name="fromSync"/> is false for the response to this device's own upload (see <see cref="HighestSeq"/>).
    /// </summary>
    public bool Upsert(ClipDto clip, byte[]? thumbnailPng = null, bool fromSync = true)
    {
        if (!UpsertLocked(clip, thumbnailPng, fromSync)) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool UpsertLocked(ClipDto clip, byte[]? thumbnailPng, bool fromSync)
    {
        lock (_gate)
        {
            if (fromSync && clip.Seq > _highestSeq) _highestSeq = clip.Seq;
            // Broadcasts can arrive out of order: ignore anything older than what we hold or already deleted.
            if (_tombstones.TryGetValue(clip.Id, out var dead) && dead >= clip.Seq) return false;
            var existing = _items.Find(i => i.Clip.Id == clip.Id);
            if (existing is not null && clip.Seq <= existing.Clip.Seq) return false;
            if (thumbnailPng is null && existing is not null && existing.Clip.Hash == clip.Hash)
                thumbnailPng = existing.ThumbnailPng;
            _tombstones.Remove(clip.Id);
            _items.RemoveAll(i => i.Clip.Id == clip.Id);
            _items.Add(new CachedClip(clip, thumbnailPng));
            _items.Sort(Compare);
            Trim(_items, capacity);
            return true;
        }
    }

    /// <summary>Removes a clip and records its tombstone. True when a held item was removed.</summary>
    public bool Remove(Guid id, long seq)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.RemoveAll(i => i.Clip.Id == id) > 0;
            if (seq > _highestSeq) _highestSeq = seq;
            if (!_tombstones.TryGetValue(id, out var prev) || seq > prev) _tombstones[id] = seq;
            TrimTombstones(_tombstones);
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>
    /// Stores a thumbnail for a held clip that still has the given content hash. False (nothing changes) when the clip is
    /// gone or its content changed, so a late fetch never brings back a deleted clip.
    /// </summary>
    public bool SetThumbnail(Guid id, string hash, byte[] thumbnailPng)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Clip.Id == id);
            if (index < 0 || _items[index].Clip.Hash != hash) return false;
            _items[index] = _items[index] with { ThumbnailPng = thumbnailPng };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
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

    /// <summary>Writes a snapshot to disk. Safe to call from several threads at once (saves are serialized).</summary>
    public void Save()
    {
        lock (_saveLock)
        {
            byte[] plaintext;
            lock (_gate)
                plaintext = JsonSerializer.SerializeToUtf8Bytes(
                    new Snapshot(_highestSeq, _lastAppliedAt, _items.ToList(), new Dictionary<Guid, long>(_tombstones), _keyId),
                    FileJson);

            var file = new byte[NonceSize + TagSize + plaintext.Length];
            var nonce = file.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plaintext, file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize));
            AtomicFile.Write(path, file);
        }
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
            var snap = JsonSerializer.Deserialize<Snapshot>(plaintext, FileJson);
            if (snap?.Items is null) return cache;
            var loaded = snap.Items.Where(i => i?.Clip is not null).ToList();
            loaded.Sort(Compare);
            Trim(loaded, capacity);
            cache._items.AddRange(loaded);
            cache._highestSeq = snap.HighestSeq;
            cache._lastAppliedAt = snap.LastAppliedAt;
            cache._keyId = snap.KeyId;
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
            cache._keyId = null;
            cache._tombstones.Clear();
        }
        return cache;
    }

    public void Clear()
    {
        lock (_saveLock)   // a save in progress finishes first, so the deleted file is not written back
        {
            lock (_gate)
            {
                _items.Clear();
                _highestSeq = 0;
                _lastAppliedAt = null;
                _keyId = null;
                _tombstones.Clear();
            }
            // The memory is clear either way; a file that can't be removed (locked, no access) must not fail the wipe.
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Overwrite it with the empty history instead, so the old one never loads again (the save lock is re-entrant).
                try { Save(); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
            }
            try { File.Delete(path + ".tmp"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static int Compare(CachedClip a, CachedClip b)
    {
        var c = b.Clip.LastCopiedAt.CompareTo(a.Clip.LastCopiedAt);
        return c != 0 ? c : b.Clip.Seq.CompareTo(a.Clip.Seq);
    }

    /// <summary>
    /// Drops the oldest clips beyond <paramref name="capacity"/> from a sorted list, except pinned ones: the Pinned tab must
    /// show every pin, however old the clip.
    /// </summary>
    private static void Trim(List<CachedClip> sorted, int capacity)
    {
        if (sorted.Count <= capacity) return;
        var keep = sorted.Take(capacity).Concat(sorted.Skip(capacity).Where(i => i.Clip.PinnedAt is not null)).ToList();
        sorted.Clear();
        sorted.AddRange(keep);
    }

    private static void TrimTombstones(Dictionary<Guid, long> t)
    {
        if (t.Count <= MaxTombstones) return;
        foreach (var k in t.OrderBy(kv => kv.Value).Take(t.Count - MaxTombstones).Select(kv => kv.Key).ToList())
            t.Remove(k);
    }

    /// <summary>
    /// The wire options, except that <see cref="ClipKind.Unknown"/> is written too: the cache keeps clips this app can't
    /// open (shown as such), which the wire format never carries.
    /// </summary>
    private static readonly JsonSerializerOptions FileJson = CreateFileJson();

    private static JsonSerializerOptions CreateFileJson()
    {
        var options = new JsonSerializerOptions(ProtocolJson.Options);
        options.Converters.Insert(0, new FileClipKindConverter());
        return options;
    }

    private sealed class FileClipKindConverter : JsonConverter<ClipKind>
    {
        public override ClipKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? reader.GetString() switch
                {
                    "text" => ClipKind.Text,
                    "link" => ClipKind.Link,
                    "image" => ClipKind.Image,
                    _ => ClipKind.Unknown,
                }
                : ClipKind.Unknown;

        public override void Write(Utf8JsonWriter writer, ClipKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                ClipKind.Text => "text",
                ClipKind.Link => "link",
                ClipKind.Image => "image",
                _ => "unknown",
            });
    }

    private sealed record Snapshot(long HighestSeq, DateTimeOffset? LastAppliedAt, List<CachedClip> Items,
        Dictionary<Guid, long>? Tombstones = null, string? KeyId = null);
}
