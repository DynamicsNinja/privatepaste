using System.Runtime.Versioning;
using System.Text;
using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform.Windows;

/// <summary>
/// The Windows clipboard. A dedicated background thread polls <c>GetClipboardSequenceNumber</c> every 250 ms and raises
/// <see cref="Changed"/> from that thread (never the UI thread). Reads prefer PNG, then DIB, then text.
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
        Task.Run(() => ClipboardRetry.RunAsync(ReadOnce, log: _log, ct: ct), ct);

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

    private ClipboardContent? ReadOnce()
    {
        using var session = new Win32Clipboard.Session();
        return ClipboardSelector.Select(f => session.Get(f switch
        {
            ClipboardFormat.Png => Win32Clipboard.PngFormat,
            ClipboardFormat.DibV5 => Win32Clipboard.CF_DIBV5,
            ClipboardFormat.Dib => Win32Clipboard.CF_DIB,
            _ => Win32Clipboard.CF_UNICODETEXT,
        }), _log);
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
