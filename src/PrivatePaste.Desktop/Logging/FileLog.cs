using System.Globalization;
using System.Text;

namespace PrivatePaste.Desktop.Logging;

/// <summary>
/// Minimal append-only diagnostics log. Rolls to <c>.1</c> (one old file kept) once the file passes <see cref="MaxBytes"/>.
/// Never log clipboard contents, passwords or tokens: callers pass fixed messages and exception types/messages only.
/// Never throws.
/// </summary>
public sealed class FileLog(string path)
{
    public const long MaxBytes = 1024 * 1024;

    private readonly object _gate = new();

    public string Path { get; } = path;

    public void Info(string message) => Write("INFO", message, null);

    public void Error(string message, Exception? e = null) => Write("ERROR", message, e);

    private void Write(string level, string message, Exception? e)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(level).Append(' ').Append(message);
        for (var inner = e; inner is not null; inner = inner.InnerException)
            line.Append(" | ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
        if (e?.StackTrace is { } trace) line.AppendLine().Append(trace);
        line.AppendLine();

        lock (_gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length >= MaxBytes) File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, line.ToString());
            }
            catch (Exception) { }   // logging must never take the app down
        }
    }
}
