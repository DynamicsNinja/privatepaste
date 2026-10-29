using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivatePaste.Desktop.Platform.Linux;

/// <summary>
/// The process that owns the X11 CLIPBOARD selection: owner window, its <c>_NET_WM_PID</c>, then the process name.
/// X11 only; null on Wayland (which hides this by design) and on any failure. Never throws; never reads window titles.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class X11SelectionOwner
{
    private const string X11 = "libX11.so.6";
    private delegate int XErrorHandler(nint display, nint errorEvent);

    [DllImport(X11)] private static extern nint XOpenDisplay(nint name);
    [DllImport(X11)] private static extern int XCloseDisplay(nint display);
    [DllImport(X11)] private static extern nint XInternAtom(nint display, string name, bool onlyIfExists);
    [DllImport(X11)] private static extern nint XGetSelectionOwner(nint display, nint selection);
    [DllImport(X11)] private static extern int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length,
        bool delete, nint reqType, out nint actualType, out int actualFormat, out nint nitems, out nint bytesAfter, out nint prop);
    [DllImport(X11)] private static extern int XFree(nint data);
    [DllImport(X11)] private static extern int XSync(nint display, bool discard);
    [DllImport(X11)] private static extern nint XSetErrorHandler(nint handler);

    // Xlib's default error handler exits the process; the owner window can vanish between two calls (BadWindow).
    private static readonly XErrorHandler Ignore = (_, _) => 0;
    private static readonly nint IgnorePointer = Marshal.GetFunctionPointerForDelegate(Ignore);   // Ignore is static, so it stays alive

    // One connection, opened on first use and closed by Close(); a busy gate (a wedged server) means "unknown", never a wait.
    private static readonly object Gate = new();
    private static nint _display;

    private static readonly Lazy<bool> Available = new(Probe);

    /// <summary>True on an X11 session where libX11 answers the selection-owner query.</summary>
    public static bool CanIdentify => Available.Value;

    private static bool InX11Session() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    private static bool Probe()
    {
        if (!InX11Session()) return false;
        try
        {
            var display = XOpenDisplay(0);
            if (display == 0) return false;
            try { XGetSelectionOwner(display, XInternAtom(display, "CLIPBOARD", false)); return true; }
            finally { XCloseDisplay(display); }
        }
        catch (Exception) { return false; }
    }

    /// <summary>Lowercase process name of the clipboard owner, or null when unknown.</summary>
    public static string? ProcessName()
    {
        if (!CanIdentify || !Monitor.TryEnter(Gate)) return null;
        try { return NameOf(OwnerPid()); }
        catch (Exception) { return null; }
        finally { Monitor.Exit(Gate); }
    }

    /// <summary>Closes the shared connection, if one was opened.</summary>
    public static void Close()
    {
        if (!Monitor.TryEnter(Gate, TimeSpan.FromSeconds(1))) return;
        try
        {
            if (_display != 0) XCloseDisplay(_display);
            _display = 0;
        }
        catch (Exception) { }
        finally { Monitor.Exit(Gate); }
    }

    private static int OwnerPid()
    {
        if (_display == 0) _display = XOpenDisplay(0);
        var display = _display;
        if (display == 0) return 0;
        var previous = XSetErrorHandler(IgnorePointer);
        try
        {
            var owner = XGetSelectionOwner(display, XInternAtom(display, "CLIPBOARD", false));
            if (owner == 0) return 0;
            const nint Cardinal = 6;   // predefined atom XA_CARDINAL
            var status = XGetWindowProperty(display, owner, XInternAtom(display, "_NET_WM_PID", false), 0, 1, false, Cardinal,
                out _, out var format, out var count, out _, out var data);
            try
            {
                // format 32 is stored as C longs: 8 bytes on 64-bit
                return status == 0 && data != 0 && format == 32 && count >= 1 ? (int)Marshal.ReadIntPtr(data) : 0;
            }
            finally { if (data != 0) XFree(data); }
        }
        finally
        {
            XSync(display, false);   // deliver any BadWindow while the ignoring handler is still installed
            XSetErrorHandler(previous);
        }
    }

    private static string? NameOf(int pid)
    {
        if (pid <= 0) return null;
        string? name = null;
        try { name = Path.GetFileName(new FileInfo($"/proc/{pid}/exe").LinkTarget); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (string.IsNullOrEmpty(name))
        {
            try { name = File.ReadAllText($"/proc/{pid}/comm").Trim(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return string.IsNullOrEmpty(name) ? null : name.ToLowerInvariant();
    }
}
