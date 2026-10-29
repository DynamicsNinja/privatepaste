using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivatePaste.Desktop.Platform.Mac;

/// <summary>
/// The bundle id of the frontmost app (NSWorkspace.sharedWorkspace.frontmostApplication.bundleIdentifier), used as the
/// source app of a copy. Best effort: the frontmost app when the copy is read is nearly always the one that copied.
/// Never throws; never reads window titles.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class FrontmostApp
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);

    public static string? BundleId()
    {
        try
        {
            var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            if (workspace == 0) return null;
            var app = Send(workspace, sel_registerName("frontmostApplication"));
            if (app == 0) return null;
            var id = Send(app, sel_registerName("bundleIdentifier"));
            if (id == 0) return null;
            var utf8 = Send(id, sel_registerName("UTF8String"));
            return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8)?.ToLowerInvariant();
        }
        catch (Exception) { return null; }
    }
}
