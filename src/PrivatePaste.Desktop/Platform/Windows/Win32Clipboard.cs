using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivatePaste.Desktop.Platform.Windows;

/// <summary>
/// Thin, synchronous Win32 clipboard layer. <see cref="Session"/> opens the clipboard and closes it on dispose; every
/// <c>OpenClipboard</c> failure is reported as <see cref="ClipboardBusyException"/> so <see cref="ClipboardRetry"/> can retry.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32Clipboard
{
    public const uint CF_DIB = 8, CF_UNICODETEXT = 13, CF_DIBV5 = 17;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>The registered clipboard format named "PNG" (used by browsers, Office, Paint.NET, ...).</summary>
    public static readonly uint PngFormat = RegisterClipboardFormat("PNG");

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr hMem);

    public static uint SequenceNumber => GetClipboardSequenceNumber();

    public sealed class Session : IDisposable
    {
        private bool _open;

        /// <exception cref="ClipboardBusyException">Another process holds the clipboard.</exception>
        public Session()
        {
            if (!OpenClipboard(IntPtr.Zero)) throw new ClipboardBusyException();
            _open = true;
        }

        public bool Has(uint format) => IsClipboardFormatAvailable(format);

        /// <summary>Copies the bytes of a clipboard format; null when absent. The clipboard keeps ownership of the handle.</summary>
        public byte[]? Get(uint format)
        {
            if (format == 0 || !IsClipboardFormatAvailable(format)) return null;
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;
            var size = checked((int)GlobalSize(handle).ToUInt64());
            if (size == 0) return null;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(ptr, bytes, 0, size);
                return bytes;
            }
            finally { GlobalUnlock(handle); }
        }

        public void Empty()
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>
        /// Puts a copy of <paramref name="data"/> on the clipboard. On success the system owns the HGLOBAL (never free it);
        /// on failure it is still ours and is freed here.
        /// </summary>
        public void Set(uint format, byte[] data)
        {
            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)data.Length);
            if (handle == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed.");
            var owned = true;
            try
            {
                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { Marshal.Copy(data, 0, ptr, data.Length); }
                finally { GlobalUnlock(handle); }

                if (SetClipboardData(format, handle) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                owned = false;
            }
            finally { if (owned) GlobalFree(handle); }
        }

        public void Dispose()
        {
            if (!_open) return;
            _open = false;
            CloseClipboard();
        }
    }
}
