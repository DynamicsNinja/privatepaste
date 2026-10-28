namespace PrivatePaste.Client.Core.Sync;

/// <summary>
/// Reconnect delays: 1 s doubling to a 30 s cap, each with ±20% jitter from the injected <see cref="Random"/>.
/// Not thread-safe; the sync engine uses it from its single loop.
/// </summary>
public sealed class Backoff(Random random)
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);
    private const double Jitter = 0.2;

    private TimeSpan _current = Initial;

    public TimeSpan Next()
    {
        var baseDelay = _current;
        _current = _current * 2 > Cap ? Cap : _current * 2;
        var factor = 1 + ((random.NextDouble() * 2) - 1) * Jitter;
        return baseDelay * factor;
    }

    public void Reset() => _current = Initial;
}
