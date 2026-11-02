using System.Security.Cryptography;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Files;

/// <summary>A file on this device about to be sent: its path, the name it travels under, its size and its content's SHA-256.</summary>
public sealed record LocalFile(string Path, string Name, long Size, string Sha256Hex)
{
    /// <summary>Reads <paramref name="path"/> once to hash it; the name is its leaf name.</summary>
    /// <exception cref="IOException">It can't be read (gone, locked).</exception>
    /// <exception cref="UnauthorizedAccessException">It may not be read.</exception>
    public static async Task<LocalFile> ReadAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var sha = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return new LocalFile(path, System.IO.Path.GetFileName(path), stream.Length, Convert.ToHexStringLower(sha));
    }

    /// <summary>The <see cref="ContentHash.ForFiles"/> of these files, in order.</summary>
    public static string HashOf(IReadOnlyList<LocalFile> files) =>
        ContentHash.ForFiles(files.Select(f => (f.Name, f.Size, f.Sha256Hex)).ToList());
}
