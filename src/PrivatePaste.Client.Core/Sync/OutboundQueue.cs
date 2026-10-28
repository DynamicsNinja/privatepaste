using System.Security.Cryptography;
using System.Text.Json;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Sync;

/// <summary>
/// Clips copied while offline, persisted on every change. Same file layout as HistoryCache:
/// nonce(12) || tag(16) || ciphertext (AES-256-GCM) with the cache key. Thread-safe.
/// </summary>
public sealed class OutboundQueue(string path, byte[] key)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly object _gate = new();
    private readonly List<Entry> _items = [];

    public int Count { get { lock (_gate) return _items.Count; } }

    public void Enqueue(ClipboardContent content, DateTimeOffset copiedAt)
    {
        lock (_gate)
        {
            _items.Add(content switch
            {
                ClipboardContent.Text t => new Entry(false, t.Value, null, copiedAt),
                ClipboardContent.Image i => new Entry(true, null, i.Png, copiedAt),
                _ => throw new ArgumentException("Unsupported content", nameof(content)),
            });
            Save();
        }
    }

    public bool TryPeek(out (ClipboardContent Content, DateTimeOffset CopiedAt) item)
    {
        lock (_gate)
        {
            if (_items.Count == 0) { item = default; return false; }
            var e = _items[0];
            item = (e.IsImage ? new ClipboardContent.Image(e.Png!) : new ClipboardContent.Text(e.Text!), e.CopiedAt);
            return true;
        }
    }

    public void Dequeue()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return;
            _items.RemoveAt(0);
            Save();
        }
    }

    /// <summary>Missing, corrupt or wrong-key files yield an empty queue; never throws.</summary>
    public static OutboundQueue Load(string path, byte[] key)
    {
        var q = new OutboundQueue(path, key);
        try
        {
            if (!File.Exists(path)) return q;
            var file = File.ReadAllBytes(path);
            if (file.Length < NonceSize + TagSize) return q;
            var plaintext = new byte[file.Length - NonceSize - TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(file.AsSpan(0, NonceSize), file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize), plaintext);
            var items = JsonSerializer.Deserialize<List<Entry>>(plaintext, ProtocolJson.Options);
            if (items is null) return q;
            q._items.AddRange(items.Where(e => e is not null && (e.IsImage ? e.Png is not null : e.Text is not null)));
        }
        catch (Exception)
        {
            q._items.Clear();
        }
        return q;
    }

    // Caller holds _gate.
    private void Save()
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_items, ProtocolJson.Options);
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

    private sealed record Entry(bool IsImage, string? Text, byte[]? Png, DateTimeOffset CopiedAt);
}
