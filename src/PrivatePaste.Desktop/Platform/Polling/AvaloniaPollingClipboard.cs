using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Desktop.Platform.Linux;
using PrivatePaste.Desktop.Platform.Mac;

namespace PrivatePaste.Desktop.Platform.Polling;

/// <summary>
/// The <see cref="IClipboardAccess"/> for macOS and Linux: Avalonia's clipboard polled every 500 ms.
/// Copied files win (file managers offer their names as text too), then text over an image. Text is compared by value; images are compared by their PNG bytes when the platform exposes <c>image/png</c> on the
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
    private static readonly DataFormat<byte[]> UriList = DataFormat.CreateBytesPlatformFormat("text/uri-list");
    private static readonly DataFormat<byte[]> GnomeCopiedFiles = DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files");
    private static readonly DataFormat<byte[]> MacFileUrl = DataFormat.CreateBytesPlatformFormat("public.file-url");

    private static readonly TimeSpan SourceAppTimeout = TimeSpan.FromSeconds(1);

    private Window? _hidden;
    private int _detectionFailureLogged;

    /// <summary>The fingerprint read of every tick: files, text or PNG only, no detection work.</summary>
    public Task<ClipboardContent?> PeekAsync(CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync<ClipboardContent?>(async () =>
        {
            if (GetClipboard() is not { } clipboard) return null;
            var files = await TryReadFilesAsync(clipboard);
            var text = files.Count > 0 ? null : await clipboard.TryGetTextAsync();
            // Text first (office apps put text plus a bitmap); the PNG is only fetched when there is no usable text.
            var png = files.Count == 0 && string.IsNullOrWhiteSpace(text) ? await TryReadPngAsync(clipboard) : null;
            return ClipboardSelection.Choose(text, () => png, files);
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
                var files = await TryReadFilesAsync(data);
                var text = files.Count > 0 ? null : await data.TryGetTextAsync();
                var png = files.Count == 0 && string.IsNullOrWhiteSpace(text) ? await TryReadPngAsync(data) : null;
                var chosen = ClipboardSelection.Choose(text, () => png, files);
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
                case ClipboardContent.Files f:
                    // A failed write is logged: whatever went wrong, the message must not carry a received file's name.
                    try
                    {
                        AddFiles(data, f.Paths);
                        await clipboard.SetDataAsync(data);
                    }
                    catch (Exception e) { throw new IOException($"Could not put the files on the clipboard ({e.GetType().Name})."); }
                    return;
                default: return;
            }
            await clipboard.SetDataAsync(data);
        });

    /// <summary>
    /// The platform's own file-list formats, written as bytes: one <c>public.file-url</c> item per file on macOS; on Linux
    /// <c>text/uri-list</c> plus GNOME's <c>x-special/gnome-copied-files</c> ("copy", so a paste copies). Unverified on
    /// real machines.
    /// </summary>
    private static void AddFiles(DataTransfer data, IReadOnlyList<string> paths)
    {
        if (OperatingSystem.IsMacOS())
        {
            foreach (var path in paths) data.Add(DataTransferItem.Create(MacFileUrl, Encoding.UTF8.GetBytes(FileUris.ToUri(path))));
            return;
        }
        var item = new DataTransferItem();
        item.Set(UriList, Encoding.UTF8.GetBytes(FileUris.ToUriList(paths)));
        item.Set(GnomeCopiedFiles, Encoding.UTF8.GetBytes(FileUris.ToGnomeCopiedFiles(paths)));
        data.Add(item);
    }

    private static async Task<IReadOnlyList<string>> TryReadFilesAsync(IClipboard clipboard)
    {
        try
        {
            using var data = await clipboard.TryGetDataAsync();
            return data is null ? [] : await TryReadFilesAsync(data);
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// The local paths of copied files: Avalonia's own file format first, then the platform formats it may not map
    /// (GNOME's list, text/uri-list, public.file-url). Empty when there are none or they can't be read; never throws.
    /// Best effort and unverified on real macOS and Linux machines.
    /// </summary>
    private static async Task<IReadOnlyList<string>> TryReadFilesAsync(IAsyncDataTransfer data)
    {
        try
        {
            var formats = data.Formats;
            if (formats.Contains(DataFormat.File) && await data.TryGetFilesAsync() is { Length: > 0 } items)
            {
                var local = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
                if (local.Count > 0) return local;
            }
            if (formats.Contains(GnomeCopiedFiles) && await ReadBytesAsync(data, GnomeCopiedFiles) is { } gnome
                && FileUris.ParseGnomeCopiedFiles(Encoding.UTF8.GetString(gnome)) is { Count: > 0 } copied)
                return copied;
            if (formats.Contains(UriList) && await ReadBytesAsync(data, UriList) is { } list
                && FileUris.ParseUriList(Encoding.UTF8.GetString(list)) is { Count: > 0 } listed)
                return listed;
            if (formats.Contains(MacFileUrl))
            {
                var urls = new List<string>();
                foreach (var item in data.Items)
                    if (await item.TryGetRawAsync(MacFileUrl) is byte[] bytes && FileUris.ToPath(Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\n')) is { } path)
                        urls.Add(path);
                return urls;
            }
        }
        catch (Exception) { }
        return [];
    }

    private static async Task<byte[]?> ReadBytesAsync(IAsyncDataTransfer data, DataFormat<byte[]> format)
    {
        foreach (var item in data.Items)
            if (await item.TryGetRawAsync(format) is byte[] { Length: > 0 } bytes) return bytes;
        return null;
    }

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
