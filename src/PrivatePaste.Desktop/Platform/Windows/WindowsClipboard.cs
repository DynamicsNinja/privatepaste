using System.Runtime.Versioning;
using System.Text;
using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform.Windows;

/// <summary>
/// The Windows clipboard. A dedicated background thread polls <c>GetClipboardSequenceNumber</c> every 250 ms and raises
/// <see cref="Changed"/> from that thread (never the UI thread). Reads prefer PNG, then DIB, then text.
/// <para>
/// A read only copies raw bytes while the clipboard is open (other applications are locked out meanwhile); DIB to PNG
/// conversion runs after it is closed. A read that gives up because another application keeps the clipboard busy re-arms
/// the watcher, so the same change is raised again on the next tick (at most <see cref="ClipboardRearm.MaxPerChange"/> times).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboard : IClipboardAccess, IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Action<string>? _log;
    private readonly ManualResetEventSlim _stop = new(false);
    private int _disposed;
    private readonly Thread _watcher;
    // Held while this app writes, so the watcher sees the whole write as one change rather than one per format.
    private readonly object _writeLock = new();
    private uint _lastSequence = Win32Clipboard.SequenceNumber;
    private readonly ClipboardRearm _rearm = new();   // guarded by _writeLock

    public event EventHandler? Changed;

    public WindowsClipboard(Action<string>? log = null)
    {
        _log = log;
        _watcher = new Thread(Watch) { IsBackground = true, Name = "PrivatePaste clipboard watcher" };
        _watcher.Start();
    }

    private void Watch()
    {
        while (!_stop.Wait(PollInterval))
        {
            try
            {
                bool changed;
                lock (_writeLock)
                {
                    var now = Win32Clipboard.SequenceNumber;
                    changed = now != _lastSequence;
                    _lastSequence = now;
                }
                if (changed) Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                _log?.Invoke("Clipboard watcher error: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    public Task<ClipboardContent?> ReadAsync(CancellationToken ct) =>
        Task.Run(async () =>
        {
            var sequence = Win32Clipboard.SequenceNumber;
            var busy = false;
            var captured = await ClipboardRetry.RunAsync(CaptureOnce, log: _log, ct: ct, gaveUp: () => busy = true)
                .ConfigureAwait(false);
            if (busy)
            {
                Rearm(sequence);
                return null;
            }
            return captured is null ? null : ClipboardSelector.Select(captured, _log);   // converts outside the session
        }, ct);

    /// <summary>Makes the watcher raise this change again on its next tick, unless a newer change is already known.</summary>
    private void Rearm(uint sequence)
    {
        lock (_writeLock)
        {
            if (_lastSequence != sequence || !_rearm.TryRearm(sequence)) return;
            _lastSequence = unchecked(sequence - 1);
        }
        _log?.Invoke("Clipboard busy; the change will be read again.");
    }

    public async Task WriteAsync(ClipboardContent content, CancellationToken ct)
    {
        if (content is not (ClipboardContent.Text or ClipboardContent.Image))
            throw new ArgumentException("Unsupported clipboard content.", nameof(content));
        // Decoding/conversion happens off the caller's thread, and before the clipboard is touched so a bad image never leaves it emptied.
        var ok = await Task.Run(async () =>
        {
            var plan = content switch
            {
                ClipboardContent.Text t => new WritePlan(Utf16Z(ToCrlf(t.Value)), null, null),
                ClipboardContent.Image i => new WritePlan(null, i.Png, DibConverter.FromPng(i.Png)),
                _ => throw new ArgumentException("Unsupported clipboard content.", nameof(content)),
            };
            return await ClipboardRetry.RunAsync(() => WriteOnce(plan), log: _log, ct: ct);
        }, ct);
        if (!ok) throw new IOException("The clipboard is in use by another application.");
    }

    private sealed record WritePlan(byte[]? Text, byte[]? Png, byte[]? Dib);

    private bool WriteOnce(WritePlan plan)
    {
        lock (_writeLock)
        {
            using (var session = new Win32Clipboard.Session())
            {
                session.Empty();
                if (plan.Text is not null) session.Set(Win32Clipboard.CF_UNICODETEXT, plan.Text);
                if (plan.Png is not null) session.Set(Win32Clipboard.PngFormat, plan.Png);
                if (plan.Dib is not null) session.Set(Win32Clipboard.CF_DIB, plan.Dib);
            }
            return true;
        }
    }

    /// <summary>Copies the needed formats' raw bytes while the clipboard is open; no decoding here.</summary>
    private static Dictionary<ClipboardFormat, byte[]> CaptureOnce()
    {
        using var session = new Win32Clipboard.Session();
        return ClipboardSelector.Capture(f => session.Get(f switch
        {
            ClipboardFormat.Png => Win32Clipboard.PngFormat,
            ClipboardFormat.DibV5 => Win32Clipboard.CF_DIBV5,
            ClipboardFormat.Dib => Win32Clipboard.CF_DIB,
            _ => Win32Clipboard.CF_UNICODETEXT,
        }));
    }

    /// <summary>LF to CRLF without doubling existing CRLF.</summary>
    private static string ToCrlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static byte[] Utf16Z(string s) => Encoding.Unicode.GetBytes(s + "\0");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Set();   // _stop is deliberately never disposed: the watcher may still be running after a timed-out Join
        if (Thread.CurrentThread != _watcher) _watcher.Join(TimeSpan.FromSeconds(1));
    }
}
