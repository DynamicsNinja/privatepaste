using Avalonia;
using Avalonia.Threading;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Desktop.Views;

namespace PrivatePaste.Desktop.Tray;

/// <summary>
/// A small borderless window at the bottom-right of the primary screen, shown for 4 s. At most one at a time:
/// a new message replaces the current one and restarts the timer. Safe to call from any thread.
/// </summary>
public sealed class ToastNotifier : INotifier
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(4);
    private const int Margin = 16;

    private ToastWindow? _window;
    private DispatcherTimer? _timer;

    public void Notify(string message) => Dispatcher.UIThread.Post(() => Show(w => w.Message = message));

    /// <summary>
    /// Shows the "clip arrived" card. Clips arriving in quick succession update the one toast in place and restart its
    /// timer; no windows stack up. Never takes the focus. Safe to call from any thread (it only posts to the UI thread).
    /// </summary>
    public void NotifyClip(ClipToast toast) => Dispatcher.UIThread.Post(() => Show(w => w.ShowClip(toast)));

    private void Show(Action<ToastWindow> fill)
    {
        _window ??= new ToastWindow();
        _timer ??= CreateTimer();
        fill(_window);
        _timer.Stop();
        if (!_window.IsVisible)
        {
            PlaceBottomRight(_window, new Size(360, 64));   // estimate, corrected once laid out
            _window.Show();
        }
        Dispatcher.UIThread.Post(() => { if (_window is { IsVisible: true } w) PlaceBottomRight(w, w.Bounds.Size); },
            DispatcherPriority.Background);
        _timer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = Duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _window?.Hide();
        };
        return timer;
    }

    private static void PlaceBottomRight(ToastWindow window, Size size)
    {
        if (window.Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(size.Width * scale);
        var height = (int)Math.Ceiling(size.Height * scale);
        var margin = (int)(Margin * scale);
        window.Position = new PixelPoint(area.Right - width - margin, area.Bottom - height - margin);
    }
}
