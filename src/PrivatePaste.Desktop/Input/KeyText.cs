using System.Runtime.InteropServices;
using System.Text;
using SharpHook.Data;

namespace PrivatePaste.Desktop.Input;

/// <summary>
/// The text a key press types on Windows, in the keyboard layout of the app in front (where the user is typing), from the
/// modifiers the hook reported. Called on the hook thread for keys the popup takes; the key itself never reaches the app.
/// </summary>
internal static class KeyText
{
    private const uint MapVkToScan = 0;
    private const uint NoKeyboardStateChange = 4;   // ToUnicodeEx flag (Windows 10 1607+): leave dead-key state alone
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkCapital = 0x14;

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, StringBuilder buffer, int bufferSize,
        uint flags, nint layout);

    /// <summary>The typed text, or null for keys that type nothing (arrows, F-keys) and dead keys (´ before a letter).</summary>
    public static string? Translate(ushort virtualKey, EventMask mask)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        var state = new byte[256];
        if ((mask & EventMask.Shift) != 0) state[VkShift] = 0x80;
        if ((mask & EventMask.Ctrl) != 0) state[VkControl] = 0x80;
        if ((mask & EventMask.Alt) != 0) state[VkMenu] = 0x80;
        if ((mask & EventMask.CapsLock) != 0) state[VkCapital] = 0x01;
        var scan = MapVirtualKeyEx(virtualKey, MapVkToScan, layout);
        var buffer = new StringBuilder(8);
        var count = ToUnicodeEx(virtualKey, scan, state, buffer, buffer.Capacity, NoKeyboardStateChange, layout);
        return count > 0 ? buffer.ToString(0, count) : null;
    }
}
