using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Desktop.Platform.Linux;
using PrivatePaste.Desktop.Platform.Mac;

namespace PrivatePaste.Desktop.Platform.Polling;

/// <summary>
/// The <see cref="IClipboardAccess"/> for macOS and Linux: Avalonia's clipboard polled every 500 ms.
/// Text wins when both text and an image are present. Text is compared by value; images are compared by their PNG bytes when the platform exposes <c>image/png</c> on the
/// clipboard (macOS and most X11 clients do); where it does not, the clipboard is text-only.
/// </summary>
public static class AvaloniaPollingClipboard
{
    public static PollingClipboard Create(Action<string>? log = null)
    {
        var clipboard = new PollingClipboard(new AvaloniaClipboardBackend(log));
        clipboard.Start();
        return clipboard;
    }
}

internal sealed class AvaloniaClipboardBackend(Action<string>? log = null) : IClipboardBackend, IDisposable
{
    private static readonly DataFormat<byte[]> Png = DataFormat.CreateBytesPlatformFormat("image/png");

    private static readonly TimeSpan SourceAppTimeout = TimeSpan.FromSeconds(1);

    private Window? _hidden;
    private int _detectionFailureLogged;

    /// <summary>The fingerprint read of every tick: text or PNG only, no detection work.</summary>
    public Task<ClipboardContent?> PeekAsync(CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync<ClipboardContent?>(async () =>
        {
            if (GetClipboard() is not { } clipboard) return null;
            var text = await clipboard.TryGetTextAsync();
            // Text first (office apps put text plus a bitmap); the PNG is only fetched when there is no usable text.
            var png = string.IsNullOrWhiteSpace(text) ? await TryReadPngAsync(clipboard) : null;
            return ClipboardSelection.Choose(text, () => png);
        });

    /// <summary>
    /// The read the coordinator consumes: content plus private-copy markers, taken from one clipboard query so the markers
    /// belong to the content. The source app is looked up off the UI thread afterwards.
    /// </summary>
    public async Task<ClipboardContent?> ReadAsync(CancellationToken ct)
    {
        var (content, isPrivate) = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (GetClipboard() is not { } clipboard) return ((ClipboardContent?)null, false);
            IAsyncDataTransfer? data = null;
            try { data = await clipboard.TryGetDataAsync(); }
            catch (Exception) { LogDetectionUnavailable(); }
            using (data)
            {
                if (data is null) return (null, false);
                var text = await data.TryGetTextAsync();
                var png = string.IsNullOrWhiteSpace(text) ? await TryReadPngAsync(data) : null;
                var chosen = ClipboardSelection.Choose(text, () => png);
                return (chosen, chosen is not null && await IsPrivateAsync(data));
            }
        });
        if (content is null) return null;
        return content with { IsPrivate = isPrivate, SourceApp = await SourceAppAsync() };
    }

    /// <summary>
    /// Marker formats through Avalonia's public format list. Unverified on real machines: if a platform does not list the
    /// marker, the copy is simply not seen as private there. If listing the formats itself fails, the copy is treated as not
    /// private and that is logged once; a listed Linux hint that can't be read fails closed (private).
    /// </summary>
    private async Task<bool> IsPrivateAsync(IAsyncDataTransfer data)
    {
        List<string> names;
        try
        {
            names = data.Formats.Select(f => f.Identifier).ToList();
        }
        catch (Exception)
        {
            LogDetectionUnavailable();
            return false;
        }
        if (OperatingSystem.IsMacOS()) return PrivateCopyMarkers.IsPrivateMac(names);
        if (!OperatingSystem.IsLinux()) return false;
        return await PrivateCopyMarkers.IsPrivateLinuxAsync(names, _ => ReadHintAsync(data));
    }

    private static async Task<string?> ReadHintAsync(IAsyncDataTransfer data)
    {
        var format = DataFormat.CreateBytesPlatformFormat(PrivateCopyMarkers.LinuxPasswordHint);
        foreach (var item in data.Items)
        {
            if (await item.TryGetRawAsync(format) is byte[] bytes) return System.Text.Encoding.UTF8.GetString(bytes);
        }
        return null;
    }

    private void LogDetectionUnavailable()
    {
        if (Interlocked.Exchange(ref _detectionFailureLogged, 1) == 0) log?.Invoke("Private-copy detection is unavailable on this system.");
    }

    /// <summary>The native lookups run on the thread pool and are abandoned after a second: a wedged X server must not stall a read.</summary>
    private static async Task<string?> SourceAppAsync()
    {
        try
        {
            return await Task.Run(SourceApp).WaitAsync(SourceAppTimeout);
        }
        catch (Exception) { return null; }
    }

    private static string? SourceApp()
    {
        if (OperatingSystem.IsMacOS()) return FrontmostApp.BundleId();
        if (OperatingSystem.IsLinux()) return X11SelectionOwner.ProcessName();
        return null;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsLinux()) X11SelectionOwner.Close();
    }

    public Task WriteAsync(ClipboardContent content, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(async Task () =>
        {
            if (GetClipboard() is not { } clipboard) return;
            var data = new DataTransfer();
            switch (content)
            {
                case ClipboardContent.Text t: data.Add(DataTransferItem.CreateText(t.Value)); break;
                case ClipboardContent.Image i: data.Add(DataTransferItem.Create(Png, i.Png)); break;
                default: return;
            }
            await clipboard.SetDataAsync(data);
        });

    private static async Task<byte[]?> TryReadPngAsync(IClipboard clipboard)
    {
        using var data = await clipboard.TryGetDataAsync();
        return data is null ? null : await TryReadPngAsync(data);
    }

    private static async Task<byte[]?> TryReadPngAsync(IAsyncDataTransfer data)
    {
        if (!data.Formats.Contains(Png)) return null;
        foreach (var item in data.Items)
        {
            if (await item.TryGetRawAsync(Png) is byte[] { Length: > 0 } bytes) return bytes;
        }
        return null;
    }

    /// <summary>Avalonia reaches the clipboard through a window; with only a tray icon, use a 1x1 invisible, never-shown one.</summary>
    private IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            return main.Clipboard;
        return (_hidden ??= new Window { Width = 1, Height = 1, ShowInTaskbar = false, Opacity = 0 }).Clipboard;
    }
}
