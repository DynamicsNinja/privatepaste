namespace PrivatePaste.Client.Core.Sync;

/// <summary>Remembers hashes of content the app itself wrote to the clipboard so they are never uploaded back.</summary>
public sealed class EchoGuard(TimeProvider time, TimeSpan window)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = [];

    /// <summary>Call right before the app writes the clipboard.</summary>
    public void Remember(string hash)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var k in _seen.Where(kv => now - kv.Value > window).Select(kv => kv.Key).ToList()) _seen.Remove(k);
            _seen[hash] = now;
        }
    }

    /// <summary>True (and consumed) if remembered within the window.</summary>
    public bool IsEcho(string hash)
    {
        lock (_gate)
        {
            if (!_seen.Remove(hash, out var at)) return false;
            return time.GetUtcNow() - at <= window;
        }
    }
}
