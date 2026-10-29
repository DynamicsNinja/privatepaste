using System.Runtime.InteropServices;
using System.Text;
using SharpHook;
using SharpHook.Simulation;
using SharpHook.Data;

namespace PrivatePaste.Desktop.Input;

/// <summary>Brings the window you were using back to the front and presses the paste shortcut in it.</summary>
public interface IPasteInjector
{
    /// <summary>Remembers the foreground window of another application (call at hotkey time, before the popup shows; not while the popup is up).</summary>
    void CapturePrevious();

    /// <summary>
    /// Gives the remembered window the focus again and returns true. False (nothing done) when none was captured, it is gone, or
    /// the user has switched to another application since: the caller must then not send the keystroke.
    /// </summary>
    bool RefocusPrevious();

    /// <summary>Simulates Ctrl+V (Cmd+V on macOS). False when the OS refused (macOS without Accessibility permission).</summary>
    bool SendPaste();
}

/// <summary>
/// SharpHook <see cref="EventSimulator"/> for the keystroke; on Windows the previous foreground window is captured with
/// <c>GetForegroundWindow</c> and restored with <c>SetForegroundWindow</c> (using the AttachThreadInput trick when plain
/// restoring is refused). On macOS and Linux the OS returns focus to the previous app when the popup closes.
/// </summary>
public sealed class PasteInjector : IPasteInjector
{
    private EventSimulator? _simulator;
    private nint _previous;

    public void CapturePrevious()
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = NativeWindows.GetForegroundWindow();
        // Nothing usable, one of our own windows, or the taskbar/tray (a tray-menu launch): forget the old one so we never refocus a stale window.
        _previous = hwnd == 0 || NativeWindows.IsOwnWindow(hwnd) || NativeWindows.IsShell(hwnd) ? 0 : hwnd;
        PopupTrace.Write($"captured {NativeWindows.Describe(hwnd)}{(_previous == 0 ? " (not usable)" : "")}");
    }

    public bool RefocusPrevious()
    {
        if (!OperatingSystem.IsWindows()) return true;   // the OS returns focus to the previous app by itself
        var hwnd = _previous;
        var foreground = NativeWindows.GetForegroundWindow();
        PopupTrace.Write($"refocus: previous {NativeWindows.Describe(hwnd)}, foreground {NativeWindows.Describe(foreground)}");
        if (hwnd != 0 && !NativeWindows.IsWindow(hwnd)) return false;
        if (!CanRefocus(hwnd, foreground)) return false;
        NativeWindows.ForceForeground(hwnd);
        PopupTrace.Write($"refocus: foreground now {NativeWindows.Describe(NativeWindows.GetForegroundWindow())}");
        return true;
    }

    /// <summary>
    /// Unchanged means: a window was captured, and the foreground is still that window or momentarily nothing (focus in
    /// transition after the popup closed). Anything else, including the taskbar or our own Settings/sign-in window, counts as
    /// "the user moved on": copy only, never paste.
    /// </summary>
    internal static bool CanRefocus(nint previous, nint foreground) =>
        previous != 0 && (foreground == 0 || foreground == previous);

    public bool SendPaste()
    {
        var modifier = OperatingSystem.IsMacOS() ? KeyCode.VcLeftMeta : KeyCode.VcLeftControl;
        try
        {
            _simulator ??= EventSimulator.Create("PrivatePaste", null);   // virtual input devices on Linux; may throw
            if (_simulator.SimulateKeyPress(modifier) != UioHookResult.Success) return false;
            var ok = _simulator.SimulateKeyPress(KeyCode.VcV) == UioHookResult.Success;
            ok &= _simulator.SimulateKeyRelease(KeyCode.VcV) == UioHookResult.Success;
            _simulator.SimulateKeyRelease(modifier);   // always release the modifier, even after a failure
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>The few Win32 calls the popup needs. Only called after an <see cref="OperatingSystem.IsWindows"/> check.</summary>
internal static class NativeWindows
{
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint GetForegroundWindowNative();

    public static nint GetForegroundWindow() => GetForegroundWindowNative();

    public static bool IsOwnWindow(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    public static bool IsShell(nint hwnd)
    {
        var name = new StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
            or "TopLevelWindowForOverflowXamlIsland" or "XamlExplorerHostIslandWindow";
    }

    /// <summary>
    /// "0x1A2B3C Chrome_WidgetWin_1 chrome" for the log: handle, window class and process name. Never the title, which
    /// can hold document names.
    /// </summary>
    public static string Describe(nint hwnd)
    {
        if (hwnd == 0) return "none";
        var name = new StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch (Exception) { process = $"pid {pid}"; }
        return $"0x{hwnd:X} {name} {process}";
    }

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    private const int VkLButton = 0x01, VkRButton = 0x02, VkMButton = 0x04;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Makes a shown window one that is never activated, by clicks either (WS_EX_NOACTIVATE), and puts it on top of
    /// every other window without taking the focus.
    /// </summary>
    public static void MakeNonActivatingTopmost(nint hwnd)
    {
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)((long)GetWindowLongPtr(hwnd, GwlExStyle) | WsExNoActivate));
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>True while any mouse button is held down.</summary>
    public static bool IsMouseButtonDown() =>
        (GetAsyncKeyState(VkLButton) & 0x8000) != 0 || (GetAsyncKeyState(VkRButton) & 0x8000) != 0
        || (GetAsyncKeyState(VkMButton) & 0x8000) != 0;

    /// <summary>True when the cursor is over the window (false when either call fails).</summary>
    public static bool IsCursorOver(nint hwnd) =>
        GetCursorPos(out var p) && GetWindowRect(hwnd, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// <summary>Physical-pixel cursor position, or null if the call fails.</summary>
    public static (int X, int Y)? CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : null;

    /// <summary>
    /// SetForegroundWindow, retried after attaching our input queue to the current foreground thread's (Windows refuses
    /// focus changes from a process that did not receive the last input otherwise).
    /// </summary>
    public static void ForceForeground(nint hwnd)
    {
        if (SetForegroundWindow(hwnd) && GetForegroundWindowNative() == hwnd) return;
        var foreground = GetForegroundWindowNative();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        try { SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(self, foregroundThread, false); }
    }
}
