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
    }

    public bool RefocusPrevious()
    {
        if (!OperatingSystem.IsWindows()) return true;   // the OS returns focus to the previous app by itself
        var hwnd = _previous;
        if (hwnd == 0 || !NativeWindows.IsWindow(hwnd)) return false;
        var foreground = NativeWindows.GetForegroundWindow();
        // In front already, or still in transition (nothing / one of our windows): fine. Another app: the user moved on.
        if (foreground != hwnd && foreground != 0 && !NativeWindows.IsOwnWindow(foreground) && !NativeWindows.IsShell(foreground))
            return false;
        NativeWindows.ForceForeground(hwnd);
        return true;
    }

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
