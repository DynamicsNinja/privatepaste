using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Files;

/// <summary>
/// A received file couldn't be written. Only the kind of failure is kept: the original exception (whose message holds the
/// path, and so the file's name) is dropped on purpose.
/// </summary>
public sealed class ClipFileWriteException(Exception cause)
    : IOException($"Could not write a received file ({cause.GetType().Name}).");

/// <summary>
/// Where file clips from other devices are written so they can be put on the clipboard: <c>root/&lt;clip id&gt;/&lt;name&gt;</c>.
/// Names come from another device and are untrusted: each goes through <see cref="FileNames.Safe"/>, so nothing lands
/// outside its clip's folder, and clashes inside one clip get " (2)", " (3)", ... A file is decrypted into
/// "<c>name.part</c>" and renamed only once its final chunk authenticated, so a file under its real name is always complete.
/// The store holds plaintext the user received; <see cref="Prune"/> keeps it to the clips still in the history (and those
/// in use) and under a size cap. Never log the names or contents.
/// </summary>
public sealed class ClipFileStore(string root, long maxBytes = 500L * 1024 * 1024)
{
    private const string PartSuffix = ".part";
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Clips being written or held (<see cref="Hold"/>), with how many holds each has. Also guards <see cref="_onClipboard"/>.</summary>
    private readonly Dictionary<Guid, int> _inUse = [];
    private Guid? _onClipboard;

    /// <summary>The store's folder (full path).</summary>
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>
    /// Writes a decoded file clip's files (see <see cref="ClipCodec.Decode"/>) and returns their paths in payload order.
    /// Files already written completely by an earlier call are reused, not fetched again. <paramref name="fetchBlob"/> gets
    /// the sealed blob of the file at an index.
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">A blob doesn't open: no file is left for it.</exception>
    /// <exception cref="InvalidDataException">Not a file clip with files.</exception>
    /// <exception cref="ClipFileWriteException">A file couldn't be written (locked, disk full, ...); its message names no file.</exception>
    public async Task<IReadOnlyList<string>> MaterializeAsync(ClipDto clip, Func<int, CancellationToken, Task<byte[]>> fetchBlob,
        ClipCodec codec, CancellationToken ct)
    {
        if (clip.Kind != ClipKind.File || clip.Files is not { Count: > 0 } files) throw new InvalidDataException("Not a file clip.");
        // Held while waiting too, so a prune meanwhile doesn't take a folder an earlier apply left for reuse.
        using var hold = Hold(clip.Id);
        // One at a time: the engine and a paste from the history may write the same clip (and its .part files) at once.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await WriteAsync(clip.Id, files, fetchBlob, codec, ct).ConfigureAwait(false);
        }
        // .NET file errors carry the full path, and with it a name from another device: never let one reach a log.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && e is not ClipFileWriteException)
        {
            throw new ClipFileWriteException(e);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<string>> WriteAsync(Guid clipId, IReadOnlyList<ClipFileInfo> files,
        Func<int, CancellationToken, Task<byte[]>> fetchBlob, ClipCodec codec, CancellationToken ct)
    {
        var folder = FolderOf(clipId);
        Directory.CreateDirectory(folder);

        // Case-insensitive on every platform, so the names don't depend on the file system. Each name also reserves its
        // ".part", so one file's download never overwrites another's finished file ("a" next to "a.part").
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>(files.Count);
        for (var n = 0; n < files.Count; n++)
        {
            var name = FileNames.Unique(FileNames.Safe(files[n].Name), c => taken.Contains(c) || taken.Contains(c + PartSuffix));
            taken.Add(name);
            taken.Add(name + PartSuffix);
            var path = Path.Combine(folder, name);
            // Belt and braces: a safe name is a leaf, so this never fires.
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), folder, StringComparison.Ordinal))
                throw new InvalidDataException("A file name left its folder.");
            paths.Add(path);

            var existing = new FileInfo(path);
            if (existing.Exists && existing.Length == files[n].Size) continue;   // written whole by an earlier apply

            var blob = await fetchBlob(n, ct).ConfigureAwait(false);
            var part = path + PartSuffix;
            try
            {
                using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await codec.DecodeBlobAsync(blob, output, ct).ConfigureAwait(false);
                File.Move(part, path, overwrite: true);
            }
            catch
            {
                TryDeleteFile(part);
                throw;
            }
        }
        // The folder's last write time orders pruning: an applied clip is the newest.
        TrySetLastWrite(folder);
        return paths;
    }

    /// <summary>Whether <paramref name="path"/> is somewhere under the store's folder (not the folder itself).</summary>
    public bool IsInside(string path)
    {
        string relative;
        try { relative = Path.GetRelativePath(Root, Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative)
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deletes the folders of clips not in <paramref name="keep"/>, then the oldest (by last write) until the rest fit in
    /// the size cap; the newest folder always stays (it may be on the clipboard from before a restart). Folders in use
    /// (being written, held, or marked as on the clipboard) are never touched. Best effort: a file another app has open
    /// stays for the next prune. Never throws.
    /// </summary>
    public void Prune(IReadOnlySet<Guid> keep)
    {
        // Once the history holds the clip on the clipboard, it keeps it: the mark goes, so deleting or clearing the clip
        // later takes its files with it, as for any other clip.
        lock (_inUse)
            if (_onClipboard is { } shown && keep.Contains(shown)) _onClipboard = null;

        DirectoryInfo[] folders;
        try { folders = new DirectoryInfo(Root).GetDirectories(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        var kept = new List<(DirectoryInfo Folder, long Bytes)>();
        foreach (var folder in folders)
        {
            var isClip = Guid.TryParseExact(folder.Name, "N", out var id);
            if (isClip && IsInUse(id)) continue;   // being written, saved or on the clipboard: never pruned
            if (isClip && keep.Contains(id)) kept.Add((folder, SizeOf(folder)));
            else TryDeleteFolder(folder);
        }

        var total = kept.Sum(k => k.Bytes);
        foreach (var (folder, bytes) in kept.OrderBy(k => k.Folder.LastWriteTimeUtc).SkipLast(1))
        {
            if (total <= maxBytes) break;
            if (TryDeleteFolder(folder)) total -= bytes;
        }
    }

    /// <summary>
    /// Keeps a clip's folder from <see cref="Prune"/> until disposed: for work that reads the files after writing them
    /// (copying them into Downloads). Writing holds the folder by itself.
    /// </summary>
    public IDisposable Hold(Guid clipId)
    {
        lock (_inUse) _inUse[clipId] = _inUse.GetValueOrDefault(clipId) + 1;
        return new Release(this, clipId);
    }

    /// <summary>
    /// The clip whose files are on the clipboard now: <see cref="Prune"/> keeps its folder (a paste reads it later) even
    /// when it isn't in the history, e.g. a pinned clip or an older search hit, until a prune finds it in the history.
    /// </summary>
    public void MarkOnClipboard(Guid clipId)
    {
        lock (_inUse) _onClipboard = clipId;
    }

    private bool IsInUse(Guid clipId)
    {
        lock (_inUse) return _onClipboard == clipId || _inUse.ContainsKey(clipId);
    }

    private void Unhold(Guid clipId)
    {
        lock (_inUse)
        {
            if (_inUse.GetValueOrDefault(clipId) <= 1) _inUse.Remove(clipId);
            else _inUse[clipId]--;
        }
    }

    private sealed class Release(ClipFileStore store, Guid clipId) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) store.Unhold(clipId); }
    }

    /// <summary>Deletes everything the store holds (sign-out, history cleared, encryption reset). Never throws.</summary>
    public void Clear()
    {
        lock (_inUse) _onClipboard = null;
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string FolderOf(Guid clipId) => Path.Combine(Root, clipId.ToString("N"));

    private static long SizeOf(DirectoryInfo folder)
    {
        try { return folder.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool TryDeleteFolder(DirectoryInfo folder)
    {
        try
        {
            folder.Delete(recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TrySetLastWrite(string folder)
    {
        try { Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
