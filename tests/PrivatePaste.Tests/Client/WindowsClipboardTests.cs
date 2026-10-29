using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Images;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Desktop.Platform;
using PrivatePaste.Desktop.Platform.Windows;
using PrivatePaste.Protocol;
using PrivatePaste.Tests.Images;

namespace PrivatePaste.Tests.Client;

/// <summary>
/// Integration tests against the REAL Windows clipboard. Opt-in (they overwrite the clipboard): set PP_CLIPBOARD_TESTS=1.
/// Without it each test returns immediately.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsClipboardTests : IDisposable
{
    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PP_CLIPBOARD_TESTS") == "1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-wclip-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task<T> WithinAsync<T>(Task<T> task, TimeSpan span)
    {
        var done = await Task.WhenAny(task, Task.Delay(span));
        Assert.True(done == task, "Timed out waiting for the clipboard change event.");
        return await task;
    }

    [Fact]
    public void Dispose_twice_is_safe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var clip = new WindowsClipboard();
        clip.Dispose();
        clip.Dispose();
    }

    [Fact]
    public async Task Dispose_while_changed_handler_blocks_does_not_throw()
    {
        if (!Enabled) return;
        var clip = new WindowsClipboard();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unhandled = new List<Exception>();
        UnhandledExceptionEventHandler h = (_, e) => unhandled.Add((Exception)e.ExceptionObject);
        AppDomain.CurrentDomain.UnhandledException += h;
        try
        {
            clip.Changed += (_, _) => { entered.TrySetResult(); Thread.Sleep(1500); };
            await clip.WriteAsync(new ClipboardContent.Text("blocker " + Guid.NewGuid()), default);
            await WithinAsync(entered.Task.ContinueWith(_ => true), TimeSpan.FromSeconds(2));
            clip.Dispose();
            clip.Dispose();
            await Task.Delay(1500);
            Assert.Empty(unhandled);
        }
        finally { AppDomain.CurrentDomain.UnhandledException -= h; }
    }

    [Fact]
    public async Task Windows_text_roundtrip()
    {
        if (!Enabled) return;
        using var clip = new WindowsClipboard();
        await clip.WriteAsync(new ClipboardContent.Text("a\nb"), default);
        var read = await clip.ReadAsync(default);
        Assert.Equal(new ClipboardContent.Text("a\r\nb"), read);
        Assert.Equal(ContentHash.ForText("a\nb"), ContentHash.ForText(((ClipboardContent.Text)read!).Value));
    }

    [Fact]
    public async Task Windows_change_event_fires()
    {
        if (!Enabled) return;
        using var clip = new WindowsClipboard();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clip.Changed += (_, _) => fired.TrySetResult();
        await clip.WriteAsync(new ClipboardContent.Text("change " + Guid.NewGuid()), default);
        await WithinAsync(fired.Task.ContinueWith(_ => true), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Windows_image_roundtrip_via_png_and_dib()
    {
        if (!Enabled) return;
        using var clip = new WindowsClipboard();
        var png = TestImages.MakePng(8, 6, 3).ToArray();
        await clip.WriteAsync(new ClipboardContent.Image(png), default);
        var read = Assert.IsType<ClipboardContent.Image>(await clip.ReadAsync(default));
        Assert.Equal(ImageHashing.HashPng(png), ImageHashing.HashPng(read.Png));
    }

    [Fact]
    public async Task Remote_image_written_to_real_clipboard_is_not_uploaded_back()
    {
        if (!Enabled) return;
        Directory.CreateDirectory(_dir);
        using var server = new PrivatePasteFactory();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var api = new PrivatePasteApi(new HttpClient(server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") });
        api.Token = (await api.LoginAsync(PrivatePasteFactory.Password, "A", "windows")).Token;
        var other = new PrivatePasteApi(server.CreateClient());
        other.Token = (await other.LoginAsync(PrivatePasteFactory.Password, "B", "windows")).Token;

        using var clip = new WindowsClipboard();
        var coordinator = new ClipboardCoordinator(clip, api,
            new HistoryCache(Path.Combine(_dir, "h.enc"), key), new OutboundQueue(Path.Combine(_dir, "q.enc"), key),
            new EchoGuard(time, TimeSpan.FromSeconds(30)), new ListNotifier(), time, () => false);

        var remote = await other.UploadImageAsync(TestImages.MakePng(16, 12, 9).ToArray(), null, false);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clip.Changed += (_, _) => changed.TrySetResult();

        await coordinator.ApplyRemoteAsync(remote, default);
        await WithinAsync(changed.Task.ContinueWith(_ => true), TimeSpan.FromSeconds(2));
        var back = Assert.IsType<ClipboardContent.Image>(await clip.ReadAsync(default));
        Assert.Equal(remote.Hash, ImageHashing.HashPng(back.Png));   // the PNG format round-trips the exact pixels
        await coordinator.OnClipboardChangedAsync(default);   // deliver the event the watcher raised

        var observer = await server.LoginAsync("Observer");
        var page = await observer.GetFromJsonAsync<ClipPage>("/api/clips?limit=50", ProtocolJson.Options);
        var only = Assert.Single(page!.Items);
        Assert.Equal("B", only.SourceDeviceName);
    }

    [Fact]
    public async Task A_read_that_gives_up_on_a_busy_clipboard_is_retried_on_a_later_tick()
    {
        if (!Enabled) return;
        using var clip = new WindowsClipboard();
        var changes = 0;
        clip.Changed += (_, _) => Interlocked.Increment(ref changes);
        await clip.WriteAsync(new ClipboardContent.Text("busy " + Guid.NewGuid()), default);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (Volatile.Read(ref changes) == 0 && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal(1, Volatile.Read(ref changes));

        // Another application holds the clipboard open (with a window: a NULL-owner open does not lock others out) while we read.
        const string script = "Add-Type -AssemblyName System.Windows.Forms; " +
            "Add-Type -Namespace W -Name C -MemberDefinition '[DllImport(\"user32.dll\")] public static extern bool OpenClipboard(System.IntPtr h); [DllImport(\"user32.dll\")] public static extern bool CloseClipboard();'; " +
            "$f = New-Object System.Windows.Forms.Form; if ([W.C]::OpenClipboard($f.Handle)) { [Console]::Out.WriteLine('opened'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null; [W.C]::CloseClipboard() | Out-Null }";
        using var holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", ["-NoProfile", "-Command", script])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        Assert.Equal("opened", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        var whileBusy = await clip.ReadAsync(default);
        await holder.StandardInput.WriteLineAsync();
        await holder.StandardInput.FlushAsync();
        await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(whileBusy);   // gave up after the retries
        until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (Volatile.Read(ref changes) < 2 && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal(2, Volatile.Read(ref changes));   // re-armed: the same change is raised again
        Assert.IsType<ClipboardContent.Text>(await clip.ReadAsync(default));
    }

    // Real-clipboard marker tests. The clipboard is put back afterwards (text or image; other formats are not restorable).
    private static async Task<ClipboardContent?> SaveAsync(WindowsClipboard clip) => await clip.ReadAsync(default);

    private static async Task RestoreAsync(WindowsClipboard clip, ClipboardContent? saved)
    {
        if (saved is not null) await clip.WriteAsync(saved, default);
    }

    /// <summary>Puts text plus the given formats on the clipboard, owned by a window of this process.</summary>
    private static void PutWithMarkers(TestWindow owner, string text, params (string Name, byte[] Data)[] markers)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var session = new Win32Clipboard.Session(owner.Handle);
                session.Empty();
                session.Set(Win32Clipboard.CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + " "));
                foreach (var (name, data) in markers) session.Set(Win32Clipboard.RegisterFormat(name), data);
                return;
            }
            catch (ClipboardBusyException) when (attempt < 50) { Thread.Sleep(20); }
        }
    }

    private static readonly byte[] Dword0 = [0, 0, 0, 0], Dword1 = [1, 0, 0, 0];

    public static TheoryData<string, byte[], bool> MarkerCases => new()
    {
        { "ExcludeClipboardContentFromMonitorProcessing", Dword1, true },
        { "Clipboard Viewer Ignore", Dword1, true },
        { "CanIncludeInClipboardHistory", Dword0, true },
        { "CanUploadToCloudClipboard", Dword0, true },
        { "CanIncludeInClipboardHistory", Dword1, false },
        { "CanUploadToCloudClipboard", Dword1, false },
    };

    [Theory]
    [MemberData(nameof(MarkerCases))]
    public async Task Read_reports_private_markers(string marker, byte[] data, bool expectedPrivate)
    {
        if (!Enabled) return;
        using var clip = new WindowsClipboard();
        var saved = await SaveAsync(clip);
        try
        {
            using var owner = new TestWindow();
            PutWithMarkers(owner, "secret " + Guid.NewGuid(), (marker, data));
            var read = Assert.IsType<ClipboardContent.Text>(await clip.ReadAsync(default));
            Assert.Equal(expectedPrivate, read.IsPrivate);
        }
        finally { await RestoreAsync(clip, saved); }
    }

    [Fact]
    public async Task Read_without_markers_is_not_private_and_names_the_owning_process()
    {
        if (!Enabled) return;
        using var clip = new WindowsClipboard();
        var saved = await SaveAsync(clip);
        try
        {
            using var owner = new TestWindow();
            PutWithMarkers(owner, "plain " + Guid.NewGuid());
            var read = Assert.IsType<ClipboardContent.Text>(await clip.ReadAsync(default));
            Assert.False(read.IsPrivate);
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant(), read.SourceApp);
        }
        finally { await RestoreAsync(clip, saved); }
    }

    [Fact]
    public async Task Source_app_falls_back_to_the_foreground_process_when_there_is_no_owner_window()
    {
        if (!Enabled) return;
        using var clip = new WindowsClipboard();
        var saved = await SaveAsync(clip);
        try
        {
            await clip.WriteAsync(new ClipboardContent.Text("ownerless " + Guid.NewGuid()), default);   // OpenClipboard(NULL): no owner
            await Task.Delay(WindowsClipboard.PollInterval * 2);   // let the watcher record the foreground app
            var read = Assert.IsType<ClipboardContent.Text>(await clip.ReadAsync(default));
            // Whatever is in front on this machine, the name is a bare lowercase executable name, or null if it could not be told.
            Assert.True(read.SourceApp is null || (read.SourceApp.Length > 0 && read.SourceApp == read.SourceApp.ToLowerInvariant()
                && !read.SourceApp.Contains('.') && !read.SourceApp.Contains('\\')));
        }
        finally { await RestoreAsync(clip, saved); }
    }

    /// <summary>
    /// A hidden message-only window on its own thread: gives the clipboard an owner in this process. It must outlive the read
    /// (destroying the owner makes the owner null), and be destroyed on the thread that created it.
    /// </summary>
    private sealed class TestWindow : IDisposable
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string cls, string? name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);

        private readonly ManualResetEventSlim _close = new(false);
        private readonly Thread _thread;
        public IntPtr Handle { get; private set; }

        public TestWindow()
        {
            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                Handle = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                ready.Set();
                _close.Wait();
                if (Handle != IntPtr.Zero) DestroyWindow(Handle);
            }) { IsBackground = true };
            _thread.Start();
            ready.Wait();
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("Could not create the clipboard owner window.");
        }

        public void Dispose()
        {
            _close.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
