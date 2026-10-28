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
    private readonly HotkeyMatcher _matcher = new();
    private long _mouse = Pack(0, 0);
    private int _started;
    private bool _disposed;

    public HotkeyService()
    {
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;
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
        _matcher.Gesture = gesture;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            _hook.RunAsync(OperatingSystem.IsWindows() ? GlobalHookType.Keyboard : GlobalHookType.All).ContinueWith(
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
        switch (_matcher.Evaluate(e.Data.KeyCode, e.RawEvent.Mask, e.IsEventSimulated))
        {
            case HotkeyDecision.Fire:
                e.SuppressEvent = true;   // honoured on Windows and macOS; ignored elsewhere
                Pressed?.Invoke(this, EventArgs.Empty);
                break;
            case HotkeyDecision.Swallow:
                e.SuppressEvent = true;
                break;
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e) => _matcher.Released(e.Data.KeyCode);
    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _matcher.Gesture = null;
        try { _hook.Dispose(); }
        catch (Exception) { }
    }
}

public enum HotkeyDecision { Ignore, Swallow, Fire }

/// <summary>
/// The pure decision logic of the hotkey, with no hook behind it: simulated events (our own Ctrl+V) are ignored, the gesture
/// fires once per physical press (auto-repeat of the held key is swallowed so it neither re-fires nor leaks to the focused
/// app until key-up), and modifiers must match exactly.
/// </summary>
public sealed class HotkeyMatcher
{
    private HotkeyGesture? _gesture;
    private bool _keyHeld;

    /// <summary>The gesture to match (null = none). Setting it re-arms the key.</summary>
    public HotkeyGesture? Gesture
    {
        get => Volatile.Read(ref _gesture);
        set { Volatile.Write(ref _gesture, value); Volatile.Write(ref _keyHeld, false); }
    }

    public HotkeyDecision Evaluate(KeyCode key, EventMask mask, bool simulated)
    {
        var gesture = Gesture;
        if (simulated || gesture is null || key != gesture.Key) return HotkeyDecision.Ignore;
        if (Held(mask, EventMask.Ctrl) != gesture.Ctrl || Held(mask, EventMask.Shift) != gesture.Shift
            || Held(mask, EventMask.Alt) != gesture.Alt || Held(mask, EventMask.Meta) != gesture.Meta)
            return HotkeyDecision.Ignore;
        if (Volatile.Read(ref _keyHeld)) return HotkeyDecision.Swallow;
        Volatile.Write(ref _keyHeld, true);
        return HotkeyDecision.Fire;
    }

    /// <summary>Key-up of the gesture key (whatever the modifiers) re-arms it.</summary>
    public void Released(KeyCode key)
    {
        if (Gesture is { } g && key == g.Key) Volatile.Write(ref _keyHeld, false);
    }

    private static bool Held(EventMask mask, EventMask flag) => (mask & flag) != 0;
}
