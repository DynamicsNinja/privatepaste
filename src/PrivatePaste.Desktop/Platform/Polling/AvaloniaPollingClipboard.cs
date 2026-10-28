using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Desktop.Platform.Polling;

/// <summary>
/// The <see cref="IClipboardAccess"/> for macOS and Linux: Avalonia's clipboard polled every 500 ms.
/// Text is compared by value. Images are compared by their PNG bytes when the platform exposes <c>image/png</c> on the
/// clipboard (macOS and most X11 clients do); where it does not, the clipboard is text-only.
/// </summary>
public static class AvaloniaPollingClipboard
{
    public static PollingClipboard Create()
    {
        var clipboard = new PollingClipboard(new AvaloniaClipboardBackend());
        clipboard.Start();
        return clipboard;
    }
}

internal sealed class AvaloniaClipboardBackend : IClipboardBackend
{
    private static readonly DataFormat<byte[]> Png = DataFormat.CreateBytesPlatformFormat("image/png");

    private Window? _hidden;

    public Task<ClipboardContent?> ReadAsync(CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync<ClipboardContent?>(async () =>
        {
            if (GetClipboard() is not { } clipboard) return null;
            if (await TryReadPngAsync(clipboard) is { } png) return (ClipboardContent)new ClipboardContent.Image(png);
            return await clipboard.TryGetTextAsync() is { Length: > 0 } text ? (ClipboardContent)new ClipboardContent.Text(text) : null;
        });

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
        if (data is null || !data.Formats.Contains(Png)) return null;
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
