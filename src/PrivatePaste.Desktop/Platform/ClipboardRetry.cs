namespace PrivatePaste.Desktop.Platform;

/// <summary>Thrown by a clipboard attempt when another application holds the clipboard open.</summary>
public sealed class ClipboardBusyException(string message = "The clipboard is in use by another application.") : Exception(message);

/// <summary>OS-agnostic retry policy for clipboard operations: another app may hold the clipboard for a few milliseconds.</summary>
public static class ClipboardRetry
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// Runs <paramref name="attempt"/> up to <paramref name="attempts"/> times while it throws <see cref="ClipboardBusyException"/>,
    /// waiting <paramref name="delay"/> between tries. After the last failed attempt it logs once and returns <c>default</c>.
    /// Other exceptions propagate immediately.
    /// </summary>
    public static async Task<T?> RunAsync<T>(Func<T> attempt, int attempts = 5, TimeSpan? delay = null,
        Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var wait = delay ?? DefaultDelay;
        for (var i = 1; i <= attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { return attempt(); }
            catch (ClipboardBusyException) when (i < attempts)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (ClipboardBusyException)
            {
                log?.Invoke($"Clipboard stayed busy after {attempts} attempts; giving up.");
                return default;
            }
        }
        return default;
    }
}
