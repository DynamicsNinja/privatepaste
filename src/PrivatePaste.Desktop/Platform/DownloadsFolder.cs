using System.Runtime.InteropServices;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop.Platform;

/// <summary>
/// The user's Downloads folder, where Save in the popup puts received files: the known folder on Windows (it may have
/// been moved), else <c>~/Downloads</c>. Files are copied in under their own names; a name already there gets " (2)",
/// " (3)", ... before the extension, so nothing is ever overwritten.
/// </summary>
public static class DownloadsFolder
{
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Path => OperatingSystem.IsWindows() && KnownFolder(FolderIdDownloads) is { } known
        ? known
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Copies <paramref name="sources"/> into <see cref="Path"/>; returns where each landed, in order.</summary>
    public static Task<IReadOnlyList<string>> SaveAsync(IReadOnlyList<string> sources, CancellationToken ct) =>
        SaveAsync(sources, Path, ct);

    /// <summary>Copies <paramref name="sources"/> into <paramref name="folder"/> (created if missing); returns where each landed.</summary>
    /// <exception cref="IOException">A file couldn't be read or written; files copied before it stay.</exception>
    public static async Task<IReadOnlyList<string>> SaveAsync(IReadOnlyList<string> sources, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var saved = new List<string>(sources.Count);
        foreach (var source in sources)
        {
            var name = FileNames.Unique(System.IO.Path.GetFileName(source),
                candidate => File.Exists(System.IO.Path.Combine(folder, candidate)) || Directory.Exists(System.IO.Path.Combine(folder, candidate)));
            var target = System.IO.Path.Combine(folder, name);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            // CreateNew: a file that appeared since the check is never overwritten (the copy fails instead).
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                try { await input.CopyToAsync(output, ct); }
                catch
                {
                    output.Close();
                    TryDelete(target);
                    throw;
                }
            }
            saved.Add(target);
        }
        return saved;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken,
        out IntPtr ppszPath);

    private static string? KnownFolder(Guid id)
    {
        try
        {
            var result = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer);
            // The buffer is freed whether the call succeeded or not.
            try { return result == 0 && Marshal.PtrToStringUni(pointer) is { Length: > 0 } path ? path : null; }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
