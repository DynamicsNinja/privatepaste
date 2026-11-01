using System.Runtime.CompilerServices;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Sync;

/// <summary>
/// Searches history beyond the local cache. The server holds only ciphertext and can't search, so this pages through
/// <c>GET /api/clips</c> (payloads only, no blobs), opens each clip and keeps the ones whose text contains the query
/// (ordinal, case-insensitive). The results are not cached.
/// </summary>
public sealed class OlderClipSearch(PrivatePasteApi api, ClipCodec codec)
{
    public const int PageSize = 200;

    /// <summary>
    /// Yields matches newest first, as each page is opened. Starts after <paramref name="afterCursor"/> (null: from the
    /// newest clip) and runs until the history ends or <paramref name="ct"/> is cancelled. An empty query yields nothing
    /// (it would match every text clip, paging through the whole history).
    /// </summary>
    public async IAsyncEnumerable<ClipDto> SearchAsync(string query, string? afterCursor,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (query.Length == 0) yield break;
        var cursor = afterCursor;
        do
        {
            var page = await api.ListAsync(cursor, PageSize, ct).ConfigureAwait(false);
            foreach (var wire in page.Items)
            {
                var clip = codec.Decode(wire);
                if (clip.Text is { } text && text.Contains(query, StringComparison.OrdinalIgnoreCase)) yield return clip;
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }
}
