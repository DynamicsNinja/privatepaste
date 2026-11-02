using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Desktop.Platform.Polling;

namespace PrivatePaste.Tests.Client;

/// <summary>Polls are driven by <see cref="PollingClipboard.TickAsync"/> (or a FakeTimeProvider), never by real time.</summary>
public class PollingClipboardTests
{
    private sealed class FakeBackend : IClipboardBackend
    {
        public ClipboardContent? Current { get; set; }
        public ClipboardContent? Written { get; private set; }
        public int Reads;
        public Task<ClipboardContent?> ReadAsync(CancellationToken ct)
        {
            var value = Current;               // capture first: Reads signals "this value was read"
            Interlocked.Increment(ref Reads);
            return Task.FromResult(value);
        }
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

    /// <summary>Tells the cheap per-tick read apart from the full read the coordinator consumes.</summary>
    private sealed class SplitReadBackend : IClipboardBackend
    {
        public int Peeks, Reads;
        public Task<ClipboardContent?> ReadAsync(CancellationToken ct) { Interlocked.Increment(ref Reads); return Task.FromResult<ClipboardContent?>(new ClipboardContent.Text("a")); }
        public Task<ClipboardContent?> PeekAsync(CancellationToken ct) { Interlocked.Increment(ref Peeks); return Task.FromResult<ClipboardContent?>(new ClipboardContent.Text("a")); }
        public Task WriteAsync(ClipboardContent content, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Poll_ticks_use_the_cheap_read_and_only_ReadAsync_does_full_detection()
    {
        var backend = new SplitReadBackend();
        var (clip, _) = Create(backend);
        await clip.TickAsync();
        await clip.TickAsync();
        Assert.Equal(2, backend.Peeks);
        Assert.Equal(0, backend.Reads);

        await clip.ReadAsync(CancellationToken.None);
        Assert.Equal(1, backend.Reads);
        Assert.Equal(2, backend.Peeks);
    }

    private static async Task<bool> WaitUntil(Func<bool> cond)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!cond() && DateTime.UtcNow < deadline) await Task.Delay(1);
        return cond();
    }

    private static (PollingClipboard Clip, Func<int> Count) Create(IClipboardBackend backend)
    {
        var clip = new PollingClipboard(backend, TimeSpan.FromMilliseconds(500));
        var count = 0;
        clip.Changed += (_, _) => Interlocked.Increment(ref count);
        return (clip, () => Volatile.Read(ref count));
    }

    [Fact]
    public async Task Raises_Changed_on_text_change_but_not_when_unchanged()
    {
        var backend = new FakeBackend { Current = new ClipboardContent.Text("a") };
        var (clip, count) = Create(backend);
        await clip.TickAsync();
        await clip.TickAsync();
        Assert.Equal(0, count());   // the initial content is the baseline

        backend.Current = new ClipboardContent.Text("b");
        await clip.TickAsync();
        Assert.Equal(1, count());
        await clip.TickAsync();
        Assert.Equal(1, count());
    }

    [Fact]
    public async Task Detects_image_byte_changes()
    {
        var backend = new FakeBackend { Current = new ClipboardContent.Image([1, 2, 3]) };
        var (clip, count) = Create(backend);
        await clip.TickAsync();
        backend.Current = new ClipboardContent.Image([1, 2, 3]);   // same bytes: no event
        await clip.TickAsync();
        Assert.Equal(0, count());
        backend.Current = new ClipboardContent.Image([1, 2, 4]);
        await clip.TickAsync();
        Assert.Equal(1, count());
    }

    [Fact]
    public async Task Detects_file_list_changes()
    {
        var backend = new FakeBackend { Current = new ClipboardContent.Files(["/home/a/one.pdf"]) };
        var (clip, count) = Create(backend);
        await clip.TickAsync();
        backend.Current = new ClipboardContent.Files(["/home/a/one.pdf"]);   // the same list: no event
        await clip.TickAsync();
        Assert.Equal(0, count());
        backend.Current = new ClipboardContent.Files(["/home/a/one.pdf", "/home/a/two.pdf"]);
        await clip.TickAsync();
        Assert.Equal(1, count());
    }

    [Fact]
    public async Task Clearing_the_clipboard_does_not_raise_Changed()
    {
        var backend = new FakeBackend { Current = new ClipboardContent.Text("a") };
        var (clip, count) = Create(backend);
        await clip.TickAsync();
        backend.Current = null;
        await clip.TickAsync();
        Assert.Equal(0, count());
    }

    [Fact]
    public async Task Write_goes_to_the_backend_and_the_next_poll_raises_Changed()
    {
        var backend = new FakeBackend { Current = new ClipboardContent.Text("a") };
        var (clip, count) = Create(backend);
        await clip.TickAsync();
        await clip.WriteAsync(new ClipboardContent.Text("x"), CancellationToken.None);
        Assert.Equal(new ClipboardContent.Text("x"), backend.Written);
        await clip.TickAsync();
        Assert.Equal(1, count());
    }

    [Fact]
    public async Task Backend_exceptions_do_not_kill_the_poller()
    {
        var backend = new ThrowingOnceBackend();
        var (clip, count) = Create(backend);
        await clip.TickAsync();          // read 1: baseline
        await clip.TickAsync();          // read 2: throws, swallowed
        backend.Current = new ClipboardContent.Text("b");
        await clip.TickAsync();          // read 3: change detected
        Assert.Equal(1, count());
    }

    [Fact]
    public async Task The_timer_loop_polls_once_per_interval_of_fake_time()
    {
        var time = new FakeTimeProvider();
        var backend = new FakeBackend { Current = new ClipboardContent.Text("a") };
        using var clip = new PollingClipboard(backend, TimeSpan.FromMilliseconds(500), time);
        var changed = 0;
        clip.Changed += (_, _) => Interlocked.Increment(ref changed);
        clip.Start();
        Assert.True(await WaitUntil(() => Volatile.Read(ref backend.Reads) == 1));   // immediate first poll = baseline

        backend.Current = new ClipboardContent.Text("b");
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(await WaitUntil(() => Volatile.Read(ref changed) == 1));
        Assert.Equal(2, Volatile.Read(ref backend.Reads));
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

    [Fact]
    public void Files_win_over_text_but_not_over_png()
    {
        var chosen = ClipboardSelection.Choose("report.pdf", () => null, ["/home/a/report.pdf"]);
        Assert.Equal(["/home/a/report.pdf"], Assert.IsType<ClipboardContent.Files>(chosen).Paths);
        // An app that puts a temp file next to a copied image: still an image.
        Assert.Equal([9], Assert.IsType<ClipboardContent.Image>(ClipboardSelection.Choose("shot.png", () => [9], ["/tmp/shot.png"])).Png);
        Assert.Equal(new ClipboardContent.Text("x"), ClipboardSelection.Choose("x", () => null, []));
    }

    private sealed class ContentComparer : IEqualityComparer<ClipboardContent?>
    {
        public bool Equals(ClipboardContent? a, ClipboardContent? b) =>
            a is ClipboardContent.Image x && b is ClipboardContent.Image y && x.Png.SequenceEqual(y.Png);
        public int GetHashCode(ClipboardContent? c) => 0;
    }
}

/// <summary>File lists on macOS and Linux clipboards: file:// URIs (text/uri-list, x-special/gnome-copied-files, public.file-url).</summary>
public class FileUrisTests
{
    [Fact]
    public void Uri_list_gives_decoded_paths_and_skips_comments_and_other_uris()
    {
        const string list = "# copied from Files\r\nfile:///home/a/r%C3%A9sum%C3%A9.pdf\r\nhttps://example.com/x.pdf\r\n"
                          + "file://localhost/home/a/two%20words.txt\r\nfile://otherhost/share/x.txt\r\n\r\n";
        Assert.Equal(["/home/a/résumé.pdf", "/home/a/two words.txt"], FileUris.ParseUriList(list));
    }

    [Fact]
    public void Gnome_copied_files_reads_copy_and_cut()
    {
        Assert.Equal(["/home/a/report.pdf", "/home/a/b c.txt"],
            FileUris.ParseGnomeCopiedFiles("copy\nfile:///home/a/report.pdf\nfile:///home/a/b%20c.txt"));
        Assert.Equal(["/home/a/x"], FileUris.ParseGnomeCopiedFiles("cut\nfile:///home/a/x\n"));
        Assert.Empty(FileUris.ParseGnomeCopiedFiles("file:///home/a/x"));   // no operation line: not this format
        Assert.Empty(FileUris.ParseGnomeCopiedFiles(""));
    }

    [Fact]
    public void Written_lists_read_back_to_the_same_paths()
    {
        string[] paths = ["/home/a/résumé.pdf", "/tmp/50% off #1?.txt"];
        Assert.Equal("file:///home/a/r%C3%A9sum%C3%A9.pdf", FileUris.ToUri(paths[0]));
        Assert.Equal(paths, FileUris.ParseUriList(FileUris.ToUriList(paths)));
        Assert.StartsWith("copy\n", FileUris.ToGnomeCopiedFiles(paths));
        Assert.Equal(paths, FileUris.ParseGnomeCopiedFiles(FileUris.ToGnomeCopiedFiles(paths)));
    }
}
