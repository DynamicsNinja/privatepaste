namespace PrivatePaste.Client.Core.Cache;

/// <summary>Write-to-temp-then-rename, so a crash never leaves a half-written file. Callers serialize writes per path.</summary>
internal static class AtomicFile
{
    private const int MoveAttempts = 10;   // waits 10, 20, ... 90 ms: under half a second in all

    public static void Write(string path, byte[] contents)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }
        // Windows: a scanner or indexer may hold the just-replaced target open for a moment (access denied / sharing violation).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < MoveAttempts && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(10 * attempt));
            }
        }
    }
}
