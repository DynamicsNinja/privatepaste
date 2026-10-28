using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Desktop.Platform.Polling;

namespace PrivatePaste.Tests.Client;

public class PollingClipboardTests
{
    private sealed class FakeBackend : IClipboardBackend
    {
        public ClipboardContent? Current { get; set; }
        public ClipboardContent? Written { get; private set; }
        public Task<ClipboardContent?> ReadAsync(CancellationToken ct) => Task.FromResult(Current);
        public Task WriteAsync(ClipboardContent content, CancellationToken ct) { Written = content; Current = content; return Task.CompletedTask; }
    }

    private sealed class ThrowingOnceBackend : IClipboardBackend
    {
        private int _calls;
        public ClipboardContent? Current { get; set; } = new ClipboardContent.Text("a");
        public Task<ClipboardContent?> ReadAsync(CancellationToken ct) =>
            Interlocked.Increment(ref _calls) == 2 ? throw new InvalidOperationException("boom") : Task.FromResult(Current);
        public Task WriteAsync(ClipboardContent content, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task<bool> WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 300 && !cond(); i++) await Task.Delay(10);
        return cond();
    }

    [Fact]
    public async Task Raises_Changed_on_text_change_but_not_when_unchanged()
    {
        var backend = new FakeBackend { Current = new ClipboardContent.Text("a") };
        using var clip = new PollingClipboard(backend, TimeSpan.FromMilliseconds(10));
        var count = 0;
        clip.Changed += (_, _) => Interlocked.Increment(ref count);
        clip.Start();
        await Task.Delay(100);
        Assert.Equal(0, count);   // the initial content is the baseline

        backend.Current = new ClipboardContent.Text("b");
        Assert.True(await WaitFor(() => count == 1));
        await Task.Delay(100);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Detects_image_byte_changes()
    {
        var backend = new FakeBackend { Current = new ClipboardContent.Image([1, 2, 3]) };
        using var clip = new PollingClipboard(backend, TimeSpan.FromMilliseconds(10));
        var count = 0;
        clip.Changed += (_, _) => Interlocked.Increment(ref count);
        clip.Start();
        await Task.Delay(60);
        backend.Current = new ClipboardContent.Image([1, 2, 3]);   // same bytes: no event
        await Task.Delay(60);
        Assert.Equal(0, count);
        backend.Current = new ClipboardContent.Image([1, 2, 4]);
        Assert.True(await WaitFor(() => count == 1));
    }

    [Fact]
    public async Task Clearing_the_clipboard_does_not_raise_Changed()
    {
        var backend = new FakeBackend { Current = new ClipboardContent.Text("a") };
        using var clip = new PollingClipboard(backend, TimeSpan.FromMilliseconds(10));
        var count = 0;
        clip.Changed += (_, _) => Interlocked.Increment(ref count);
        clip.Start();
        await Task.Delay(50);
        backend.Current = null;
        await Task.Delay(100);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Write_goes_to_the_backend_and_raises_Changed()
    {
        var backend = new FakeBackend { Current = new ClipboardContent.Text("a") };
        using var clip = new PollingClipboard(backend, TimeSpan.FromMilliseconds(10));
        var count = 0;
        clip.Changed += (_, _) => Interlocked.Increment(ref count);
        clip.Start();
        await Task.Delay(50);
        await clip.WriteAsync(new ClipboardContent.Text("x"), CancellationToken.None);
        Assert.Equal(new ClipboardContent.Text("x"), backend.Written);
        Assert.True(await WaitFor(() => count >= 1));
    }

    [Fact]
    public async Task Backend_exceptions_do_not_kill_the_poller()
    {
        var backend = new ThrowingOnceBackend();
        using var clip = new PollingClipboard(backend, TimeSpan.FromMilliseconds(10));
        var count = 0;
        clip.Changed += (_, _) => Interlocked.Increment(ref count);
        clip.Start();
        await Task.Delay(50);
        backend.Current = new ClipboardContent.Text("b");
        Assert.True(await WaitFor(() => count == 1));
    }
}

public class ClipboardSelectionTests
{
    [Fact]
    public void Prefers_text_when_both_text_and_png_are_present()
    {
        var chosen = ClipboardSelection.Choose("hello", () => [1, 2, 3]);
        Assert.Equal(new ClipboardContent.Text("hello"), chosen);
    }

    [Fact]
    public void Uses_png_when_text_is_missing_or_whitespace()
    {
        Assert.Equal(new ClipboardContent.Image([9]), ClipboardSelection.Choose(null, () => [9]), new ContentComparer());
        Assert.IsType<ClipboardContent.Image>(ClipboardSelection.Choose("  \n", () => [9]));
    }

    [Fact]
    public void Falls_back_to_whitespace_text_then_null()
    {
        Assert.Equal(new ClipboardContent.Text(" "), ClipboardSelection.Choose(" ", () => null));
        Assert.Null(ClipboardSelection.Choose(null, () => null));
        Assert.Null(ClipboardSelection.Choose("", () => null));
    }

    [Fact]
    public void Does_not_read_the_png_when_text_is_usable()
    {
        var read = false;
        ClipboardSelection.Choose("x", () => { read = true; return [1]; });
        Assert.False(read);
    }

    private sealed class ContentComparer : IEqualityComparer<ClipboardContent?>
    {
        public bool Equals(ClipboardContent? a, ClipboardContent? b) =>
            a is ClipboardContent.Image x && b is ClipboardContent.Image y && x.Png.SequenceEqual(y.Png);
        public int GetHashCode(ClipboardContent? c) => 0;
    }
}
