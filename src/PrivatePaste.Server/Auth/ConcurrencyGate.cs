namespace PrivatePaste.Server.Auth;

/// <summary>
/// Admits at most <c>maxConcurrent</c> holders at once and lets at most <c>maxQueued</c> more wait; anyone beyond
/// that is turned away immediately instead of piling up.
/// </summary>
public sealed class ConcurrencyGate(int maxConcurrent, int maxQueued)
{
    private readonly SemaphoreSlim _slots = new(maxConcurrent, maxConcurrent);
    private int _pending; // holders + waiters

    /// <summary>Holders plus waiters right now.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Returns a lease to dispose when done, or null (synchronously) if the gate and its queue are full.</summary>
    public async Task<IDisposable?> TryEnterAsync(CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _pending) > maxConcurrent + maxQueued)
        {
            Interlocked.Decrement(ref _pending);
            return null;
        }

        try
        {
            await _slots.WaitAsync(ct);
        }
        catch
        {
            Interlocked.Decrement(ref _pending);
            throw;
        }
        return new Lease(this);
    }

    private sealed class Lease(ConcurrencyGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            gate._slots.Release();
            Interlocked.Decrement(ref gate._pending);
        }
    }
}
