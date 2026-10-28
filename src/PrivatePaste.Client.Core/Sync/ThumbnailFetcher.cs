using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Sync;

/// <summary>
/// Fetches thumbnails of image clips in the background and stores them in the cache. Best effort: failures are swallowed
/// and the clip simply shows without a thumbnail. At most <see cref="MaxConcurrent"/> fetches run at once; each clip is
/// fetched at most once at a time; nothing here runs on (or waits for) the coordinator's gate.
/// </summary>
public sealed class ThumbnailFetcher(PrivatePasteApi api, HistoryCache cache)
{
    public const int MaxConcurrent = 2;

    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly object _gate = new();
    private readonly HashSet<Guid> _pending = [];

    /// <summary>A thumbnail was stored in the cache (raised on a pool thread; the cache needs saving).</summary>
    public event EventHandler? Stored;

    /// <summary>Starts a fetch for an image clip the cache holds without a thumbnail; anything else is ignored.</summary>
    public void Request(ClipDto clip)
    {
        if (clip.Kind != ClipKind.Image) return;
        if (!cache.Items.Any(i => i.Clip.Id == clip.Id && i.ThumbnailPng is null)) return;
        lock (_gate)
            if (!_pending.Add(clip.Id)) return;
        _ = Task.Run(() => FetchAsync(clip));
    }

    private async Task FetchAsync(ClipDto clip)
    {
        try
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                var png = await api.GetThumbnailAsync(clip.Id).ConfigureAwait(false);
                if (png.Length > 0 && cache.SetThumbnail(clip.Id, clip.Hash, png)) Stored?.Invoke(this, EventArgs.Empty);
            }
            finally { _slots.Release(); }
        }
        catch (Exception) { }   // offline, deleted meanwhile, signed out (disposed client), a throwing handler: no thumbnail
        finally
        {
            lock (_gate) _pending.Remove(clip.Id);
        }
    }
}
