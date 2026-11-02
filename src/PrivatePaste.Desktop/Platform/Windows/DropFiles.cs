using System.Runtime.InteropServices;
using System.Text;

namespace PrivatePaste.Desktop.Platform.Windows;

/// <summary>
/// The bytes of a CF_HDROP file list: a 20-byte DROPFILES header (pFiles, pt.x, pt.y, fNC, fWide) and then the full
/// paths, each ending with a NUL, the list with one more. Built with <c>fWide = 1</c> (UTF-16). Read from the bytes
/// themselves (what DragQueryFileW reads), so it runs after the clipboard is closed and needs no clipboard to test.
/// </summary>
public static class DropFiles
{
    private const int HeaderSize = 20;

    public static byte[] Build(IReadOnlyList<string> paths)
    {
        var list = new StringBuilder();
        foreach (var path in paths) list.Append(path).Append('\0');
        list.Append('\0');
        var names = Encoding.Unicode.GetBytes(list.ToString());
        var bytes = new byte[HeaderSize + names.Length];
        BitConverter.GetBytes(HeaderSize).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 16);
        names.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    /// <summary>The paths in the list; empty when the bytes aren't a readable DROPFILES. Never throws.</summary>
    public static IReadOnlyList<string> Parse(byte[] bytes)
    {
        if (bytes.Length < HeaderSize) return [];
        var start = BitConverter.ToInt32(bytes, 0);
        if (start < HeaderSize || start >= bytes.Length) return [];
        var wide = BitConverter.ToInt32(bytes, 16) != 0;
        var text = wide ? Encoding.Unicode.GetString(bytes, start, (bytes.Length - start) & ~1) : Ansi(bytes, start);
        var paths = new List<string>();
        foreach (var name in text.Split('\0'))
        {
            if (name.Length == 0) break;   // the empty entry is the end of the list
            paths.Add(name);
        }
        return paths;
    }

    /// <summary>An old-style list in the system code page (Windows' ANSI code page, read as the OS reads it).</summary>
    private static string Ansi(byte[] bytes, int start)
    {
        var copy = new byte[bytes.Length - start + 2];   // two extra NULs, so a list without its end can't be read past
        Array.Copy(bytes, start, copy, 0, bytes.Length - start);
        var text = new StringBuilder();
        var handle = GCHandle.Alloc(copy, GCHandleType.Pinned);
        try
        {
            var offset = 0;
            while (offset < copy.Length && copy[offset] != 0)
            {
                var name = Marshal.PtrToStringAnsi(handle.AddrOfPinnedObject() + offset) ?? "";
                text.Append(name).Append('\0');
                var end = Array.IndexOf(copy, (byte)0, offset);
                offset = end + 1;
            }
        }
        finally { handle.Free(); }
        return text.ToString();
    }
}
