using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Images;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
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
}
