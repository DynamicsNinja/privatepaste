using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrivatePaste.Tests.Client;

/// <summary>Opt-in (PP_WINDOW_TESTS=1, Windows only): launches the real app and checks the sign-in window's Win32 style bits.</summary>
public class WindowsChromeTests
{
    private const long Caption = 0x00C00000, ThickFrame = 0x00040000, MinBox = 0x00020000, MaxBox = 0x00010000, SysMenu = 0x00080000;

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);

    [Fact]
    public void Sign_in_window_has_resize_snap_and_system_menu_bits_but_no_caption()
    {
        if (Environment.GetEnvironmentVariable("PP_WINDOW_TESTS") != "1" || !OperatingSystem.IsWindows()) return;

        var exe = Path.Combine(AppContext.BaseDirectory, "PrivatePaste.exe");
        Assert.True(File.Exists(exe), exe);
        var dataDir = Path.Combine(Path.GetTempPath(), "pp-win-" + Guid.NewGuid().ToString("N"));
        using var proc = Process.Start(new ProcessStartInfo(exe, $"--data-dir \"{dataDir}\"") { UseShellExecute = false })!;
        try
        {
            IntPtr hwnd = IntPtr.Zero;
            for (var i = 0; i < 40 && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(250);
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out var pid);
                    if (pid == proc.Id && IsWindowVisible(h)) { hwnd = h; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            Assert.NotEqual(IntPtr.Zero, hwnd);
            Thread.Sleep(500); // let the post-open frame fix-up run
            var style = GetWindowLongPtr(hwnd, -16).ToInt64();
            Console.WriteLine($"style=0x{style:X}");
            Assert.Equal(ThickFrame, style & ThickFrame);
            Assert.Equal(MinBox, style & MinBox);
            Assert.Equal(MaxBox, style & MaxBox);
            Assert.Equal(SysMenu, style & SysMenu);
            Assert.NotEqual(Caption, style & Caption);
        }
        finally
        {
            if (!proc.HasExited) proc.Kill(true);
            try { Directory.Delete(dataDir, true); } catch { }
        }
    }
}
