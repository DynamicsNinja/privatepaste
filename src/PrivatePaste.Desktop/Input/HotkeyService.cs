using SharpHook;
using SharpHook.Data;

namespace PrivatePaste.Desktop.Input;

/// <summary>
/// The global hotkey. One SharpHook hook runs for the lifetime of the service; changing the gesture swaps the match in
/// place (<see cref="Register"/>), so there is never a window with two registrations or none. Dispose stops the hook.
/// <para>
/// A <see cref="SimpleGlobalHook"/> is used instead of the task-pool one because the handler must run synchronously on
/// the hook thread to be able to swallow the matching keystroke (otherwise the focused app would also receive
/// Ctrl+Shift+V, e.g. "paste unformatted", before the popup appears). The handler therefore only reads a volatile field
/// and raises <see cref="Pressed"/>; subscribers must return quickly and marshal to the UI thread themselves.
/// </para>
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly SimpleGlobalHook _hook = new();
    private volatile HotkeyGesture? _gesture;
    private long _mouse = Pack(0, 0);
    private int _started;
    private bool _disposed;

    public HotkeyService()
    {
        _hook.KeyPressed += OnKeyPressed;
        _hook.MouseMoved += (_, e) => _mouse = Pack(e.Data.X, e.Data.Y);
        _hook.MouseDragged += (_, e) => _mouse = Pack(e.Data.X, e.Data.Y);
        _hook.MousePressed += (_, e) => _mouse = Pack(e.Data.X, e.Data.Y);
    }

    /// <summary>Raised on the hook thread when the gesture is pressed.</summary>
    public event EventHandler? Pressed;

    /// <summary>The hook could not start (macOS Input Monitoring permission, Wayland, ...) or stopped unexpectedly.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The last mouse position the hook saw (screen pixels); used where there is no native cursor API.</summary>
    public (int X, int Y) LastMousePosition
    {
        get
        {
            var v = Interlocked.Read(ref _mouse);
            return ((int)(v >> 32), (int)(v & 0xFFFFFFFF));
        }
    }

    /// <summary>Sets (or replaces) the gesture and starts the hook on first use.</summary>
    public void Register(HotkeyGesture gesture)
    {
        if (_disposed) return;
        _gesture = gesture;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            _hook.RunAsync().ContinueWith(
                t => { if (!_disposed && t.Exception is { } e) Failed?.Invoke(this, e.GetBaseException()); },
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception e)
        {
            Failed?.Invoke(this, e);
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var gesture = _gesture;
        if (gesture is null || e.Data.KeyCode != gesture.Key) return;
        var mask = e.RawEvent.Mask;
        // Exact modifier match: Ctrl+Shift+V must not fire for Ctrl+Shift+Alt+V.
        if (Held(mask, EventMask.Ctrl) != gesture.Ctrl || Held(mask, EventMask.Shift) != gesture.Shift
            || Held(mask, EventMask.Alt) != gesture.Alt || Held(mask, EventMask.Meta) != gesture.Meta)
            return;
        e.SuppressEvent = true;   // honoured on Windows and macOS; ignored elsewhere
        Pressed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Held(EventMask mask, EventMask flag) => (mask & flag) != 0;

    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gesture = null;
        try { _hook.Dispose(); }
        catch (Exception) { }
    }
}
