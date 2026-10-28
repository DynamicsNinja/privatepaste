using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace PrivatePaste.Desktop.Views;

/// <summary>
/// With WindowDecorations.BorderOnly Avalonia creates a window with THICKFRAME / MINIMIZEBOX / MAXIMIZEBOX but no SYSMENU,
/// which disables Alt+Space. Adds WS_SYSMENU (never WS_CAPTION, so no native caption buttons).
/// </summary>
internal static class WindowsFrame
{
    private const int GwlStyle = -16;
    private const long WsSysMenu = 0x00080000;
    private const uint SwpFlags = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020; // NOSIZE NOMOVE NOZORDER NOACTIVATE FRAMECHANGED

    public static void EnsureSystemMenu(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        if ((style & WsSysMenu) != 0) return;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style | WsSysMenu));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpFlags);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
