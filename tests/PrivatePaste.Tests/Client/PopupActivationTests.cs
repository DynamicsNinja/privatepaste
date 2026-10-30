using Avalonia;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Desktop.Input;
using PrivatePaste.Desktop.Views;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Client;

public class PopupActivationTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _log = [];
    private readonly List<string> _toasts = [];
    private bool _paused;
    private bool _signedIn = true;
    private Func<Guid, CancellationToken, Task<ClipDto>> _restore = (_, _) => throw new InvalidOperationException("restore not configured");
    private Func<ClipDto, CancellationToken, Task<bool>> _apply;
    private bool _pasteResult = true;
    private bool _canRefocus = true;

    private sealed class FakeInjector(List<string> log, Func<bool> result, Func<bool> refocus) : IPasteInjector
    {
        public void CapturePrevious() { }
        public bool RefocusPrevious() { if (!refocus()) return false; log.Add("refocus"); return true; }
        public bool SendPaste() { log.Add("paste"); return result(); }
    }

    // Like the coordinator: nothing is written (false) while paused.
    public PopupActivationTests() => _apply = (_, _) => Task.FromResult(!_paused);

    private static ClipDto Clip(string text = "hello") =>
        new(Guid.NewGuid(), 1, ClipKind.Text, text, "h", null, null, 5, null, "PC", DateTimeOffset.UtcNow);

    private PopupActivation Create(bool macos = false) => new(
        () => _signedIn, () => _paused,
        (id, ct) => { _log.Add("restore"); return _restore(id, ct); },
        (c, ct) => { _log.Add("apply"); return _apply(c, ct); },
        (text, _) => { _log.Add("local:" + text); return Task.CompletedTask; },
        new FakeInjector(_log, () => _pasteResult, () => _canRefocus), _toasts.Add, _time, macos);

    /// <summary>Runs the flow, advancing the fake clock past the 60 ms paste delay once it is waiting there.</summary>
    private async Task RunAsync(PopupActivation a, ClipDto clip)
    {
        var task = a.ActivateAsync(clip);
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            await Task.Delay(5);
            _time.Advance(TimeSpan.FromMilliseconds(60));
        }
        await task;
    }

    [Fact]
    public async Task Happy_path_restores_applies_refocuses_then_pastes()
    {
        var a = Create();
        var clip = Clip();
        _restore = (_, _) => Task.FromResult(clip with { Seq = 2 });
        await RunAsync(a, clip);
        Assert.Equal(["restore", "apply", "refocus", "paste"], _log);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Paste_waits_60ms_after_refocus()
    {
        var a = Create();
        var clip = Clip();
        _restore = (_, _) => Task.FromResult(clip);
        var task = a.ActivateAsync(clip);
        await Task.Delay(100);
        Assert.Equal(["restore", "apply", "refocus"], _log);
        _time.Advance(TimeSpan.FromMilliseconds(59));
        await Task.Delay(30);
        Assert.DoesNotContain("paste", _log);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await task;
        Assert.Equal("paste", _log[^1]);
    }

    [Fact]
    public async Task Offline_uses_cached_clip_without_restore()
    {
        var a = Create();
        var clip = Clip();
        ClipDto? applied = null;
        _restore = (_, _) => throw new HttpRequestException("offline");
        _apply = (c, _) => { applied = c; return Task.FromResult(true); };
        await RunAsync(a, clip);
        Assert.Same(clip, applied);
        Assert.Equal("paste", _log[^1]);
    }

    [Fact]
    public async Task Clipboard_write_failure_toasts_and_sends_no_keystroke()
    {
        var a = Create();
        var clip = Clip();
        _restore = (_, _) => Task.FromResult(clip);
        _apply = (_, _) => throw new InvalidOperationException("clipboard locked");
        await RunAsync(a, clip);
        Assert.DoesNotContain("paste", _log);
        Assert.DoesNotContain("refocus", _log);
        Assert.Single(_toasts);
    }

    [Fact]
    public async Task Deleted_clip_toasts_and_sends_no_keystroke()
    {
        var a = Create();
        _restore = (_, _) => throw new ApiException(System.Net.HttpStatusCode.NotFound, "gone");
        await RunAsync(a, Clip());
        Assert.DoesNotContain("paste", _log);
        Assert.Single(_toasts);
    }

    [Fact]
    public async Task Paused_copies_text_locally_without_restore_or_coordinator()
    {
        _paused = true;
        var a = Create();
        await RunAsync(a, Clip("local text"));
        Assert.Equal(["local:local text", "refocus", "paste"], _log);
    }

    [Fact]
    public async Task Paused_image_is_not_fetched_and_not_pasted()
    {
        _paused = true;
        var a = Create();
        var image = new ClipDto(Guid.NewGuid(), 1, ClipKind.Image, null, "h", 10, 10, 100, null, "PC", DateTimeOffset.UtcNow);
        await RunAsync(a, image);
        Assert.Empty(_log);
        Assert.Single(_toasts);
    }

    [Fact]
    public async Task Pausing_during_restore_aborts_instead_of_pasting_stale_clipboard()
    {
        var a = Create();
        var clip = Clip();
        _restore = (_, _) => { _paused = true; return Task.FromResult(clip); };
        await RunAsync(a, clip);
        Assert.DoesNotContain("paste", _log);
    }

    [Fact]
    public async Task Signed_out_does_nothing_but_toast()
    {
        _signedIn = false;
        var a = Create();
        await RunAsync(a, Clip());
        Assert.Empty(_log);
        Assert.Single(_toasts);
    }

    [Fact]
    public async Task Macos_paste_failure_falls_back_to_copy_with_one_time_toast()
    {
        var a = Create(macos: true);
        var clip = Clip();
        _restore = (_, _) => Task.FromResult(clip);
        _pasteResult = false;
        await RunAsync(a, clip);
        await RunAsync(a, clip);
        Assert.Equal([PopupActivation.AccessibilityToast], _toasts);
    }

    [Fact]
    public async Task Slow_restore_times_out_after_2_5s_and_uses_cached_clip()
    {
        var a = Create();
        var clip = Clip();
        ClipDto? applied = null;
        _restore = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return clip; };
        _apply = (c, _) => { applied = c; return Task.FromResult(true); };
        var task = a.ActivateAsync(clip);
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromMilliseconds(2499));
        await Task.Delay(30);
        Assert.Null(applied);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 100 && !task.IsCompleted; i++) { await Task.Delay(5); _time.Advance(TimeSpan.FromMilliseconds(60)); }
        await task;
        Assert.Same(clip, applied);
        Assert.Equal("paste", _log[^1]);
    }

    [Fact]
    public async Task Foreground_changed_means_copy_only_with_toast()
    {
        var a = Create();
        var clip = Clip();
        _restore = (_, _) => Task.FromResult(clip);
        _canRefocus = false;
        await RunAsync(a, clip);
        Assert.Equal(["restore", "apply"], _log);
        Assert.Equal([PopupActivation.CopiedToast], _toasts);
    }



    [Fact]
    public async Task No_captured_window_means_copy_only_with_toast()
    {
        // e.g. tray "Open history": the injector captured nothing, so RefocusPrevious reports false.
        var a = Create();
        var clip = Clip();
        _restore = (_, _) => Task.FromResult(clip);
        _canRefocus = false;
        await RunAsync(a, clip);
        Assert.DoesNotContain("refocus", _log);
        Assert.DoesNotContain("paste", _log);
        Assert.Equal([PopupActivation.CopiedToast], _toasts);
    }

    [Fact]
    public async Task A_write_held_past_the_deadline_gives_no_paste()
    {
        var a = Create();
        var clip = Clip();
        _restore = (_, _) => Task.FromResult(clip);
        _apply = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return true; };   // e.g. the coordinator gate is held
        var task = a.ActivateAsync(clip);
        await Task.Delay(50);
        _time.Advance(PopupActivation.Deadline - TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);
        Assert.False(task.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["restore", "apply"], _log);
        Assert.Equal([PopupActivation.CouldNotCopyToast], _toasts);
    }

    [Fact]
    public async Task A_write_finishing_after_the_deadline_copies_without_pasting()
    {
        var a = Create();
        var clip = Clip();
        var write = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restore = (_, _) => Task.FromResult(clip);
        _apply = (_, _) => write.Task;   // already writing: it does not stop at the deadline
        var task = a.ActivateAsync(clip);
        await Task.Delay(50);
        _time.Advance(PopupActivation.Deadline);
        await Task.Delay(30);
        write.SetResult(true);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["restore", "apply"], _log);
        Assert.Equal([PopupActivation.CopiedToast], _toasts);
    }

    [Fact]
    public async Task Nothing_written_gives_no_paste()
    {
        var a = Create();
        var image = new ClipDto(Guid.NewGuid(), 1, ClipKind.Image, null, "h", 10, 10, 100, null, "PC", DateTimeOffset.UtcNow);
        _restore = (_, _) => Task.FromResult(image);
        _apply = (_, _) => Task.FromResult(false);   // e.g. the downloaded image did not decode
        await RunAsync(a, image);
        Assert.Equal(["restore", "apply"], _log);
        Assert.Equal([PopupActivation.CouldNotCopyToast], _toasts);
    }

    [Fact]
    public async Task An_undecodable_image_from_the_server_gives_no_paste()
    {
        using var server = new PrivatePasteFactory();
        var handler = new SwitchHandler(server.Server.CreateHandler());
        var api = new PrivatePasteApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        api.Token = (await api.LoginAsync(PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "A", "windows")).Token;
        var image = await api.UploadImageAsync(PrivatePaste.Tests.Images.TestImages.MakePng(4, 4, 1).ToArray(), null, false);
        handler.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/image") && r.Method == HttpMethod.Get
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html>captive portal</html>") }
            : null;
        var clipboard = new FakeClipboard();
        var coordinator = new PrivatePaste.Client.Core.Sync.ClipboardCoordinator(clipboard, api,
            new PrivatePaste.Client.Core.Cache.HistoryCache(Path.Combine(Path.GetTempPath(), "pp-act-" + Guid.NewGuid() + ".dat"), new byte[32]),
            new PrivatePaste.Client.Core.Sync.OutboundQueue(Path.Combine(Path.GetTempPath(), "pp-actq-" + Guid.NewGuid() + ".dat"), new byte[32]),
            new PrivatePaste.Client.Core.Sync.EchoGuard(_time, TimeSpan.FromSeconds(30)), new ListNotifier(), _time, () => false);
        _restore = api.RestoreAsync;
        _apply = coordinator.ApplyRemoteAsync;

        await RunAsync(Create(), image);

        Assert.Empty(clipboard.Writes);
        Assert.DoesNotContain("paste", _log);
        Assert.Equal([PopupActivation.CouldNotCopyToast], _toasts);
    }

    [Fact]
    public void Placement_is_clamped_to_the_working_area()
    {
        var work = new PixelRect(0, 0, 1920, 1040);
        var size = new PixelSize(420, 480);
        Assert.Equal(new PixelPoint(100, 100), PopupWindow.Place(new PixelPoint(100, 100), size, work));
        Assert.Equal(new PixelPoint(1500, 560), PopupWindow.Place(new PixelPoint(1900, 1030), size, work));
        Assert.Equal(new PixelPoint(0, 0), PopupWindow.Place(new PixelPoint(-50, -20), size, work));
    }
}
