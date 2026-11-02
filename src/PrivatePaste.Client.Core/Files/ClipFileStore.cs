using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Files;

/// <summary>
/// Where file clips from other devices are written so they can be put on the clipboard: <c>root/&lt;clip id&gt;/&lt;name&gt;</c>.
/// Names come from another device and are untrusted: each goes through <see cref="FileNames.Safe"/>, so nothing lands
/// outside its clip's folder, and clashes inside one clip get " (2)", " (3)", ... A file is decrypted into
/// "<c>name.part</c>" and renamed only once its final chunk authenticated, so a file under its real name is always complete.
/// The store holds plaintext the user received; <see cref="Prune"/> keeps it to the clips still in the history and under a
/// size cap. Never log the names or contents.
/// </summary>
public sealed class ClipFileStore(string root, long maxBytes = 500L * 1024 * 1024)
{
    private const string PartSuffix = ".part";

    /// <summary>The store's folder (full path).</summary>
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>
    /// Writes a decoded file clip's files (see <see cref="ClipCodec.Decode"/>) and returns their paths in payload order.
    /// Files already written completely by an earlier call are reused, not fetched again. <paramref name="fetchBlob"/> gets
    /// the sealed blob of the file at an index.
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">A blob doesn't open: no file is left for it.</exception>
    /// <exception cref="InvalidDataException">Not a file clip with files.</exception>
    public async Task<IReadOnlyList<string>> MaterializeAsync(ClipDto clip, Func<int, CancellationToken, Task<byte[]>> fetchBlob,
        ClipCodec codec, CancellationToken ct)
    {
        if (clip.Kind != ClipKind.File || clip.Files is not { Count: > 0 } files) throw new InvalidDataException("Not a file clip.");
        var folder = FolderOf(clip.Id);
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
    /// the size cap; the newest folder always stays (it may be on the clipboard). Best effort: a file in use stays for the
    /// next prune. Never throws.
    /// </summary>
    public void Prune(IReadOnlySet<Guid> keep)
    {
        DirectoryInfo[] folders;
        try { folders = new DirectoryInfo(Root).GetDirectories(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        var kept = new List<(DirectoryInfo Folder, long Bytes)>();
        foreach (var folder in folders)
        {
            if (Guid.TryParseExact(folder.Name, "N", out var id) && keep.Contains(id)) kept.Add((folder, SizeOf(folder)));
            else TryDeleteFolder(folder);
        }

        var total = kept.Sum(k => k.Bytes);
        foreach (var (folder, bytes) in kept.OrderBy(k => k.Folder.LastWriteTimeUtc).SkipLast(1))
        {
            if (total <= maxBytes) break;
            if (TryDeleteFolder(folder)) total -= bytes;
        }
    }

    /// <summary>Deletes everything the store holds (sign-out, history cleared, encryption reset). Never throws.</summary>
    public void Clear()
    {
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
