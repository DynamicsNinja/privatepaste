using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Protocol;
using PrivatePaste.Tests.Images;

namespace PrivatePaste.Tests.Client;

public partial class ClipboardCoordinatorTests
{
    private ClipDto RemoteText(string text) =>
        new(Guid.NewGuid(), 1, ClipKind.Text, text, ContentHash.ForText(text), null, null, 1, null, "B", _time.GetUtcNow());

    [Fact]
    public async Task Live_copy_hitting_503_is_queued()
    {
        await StartAsync();
        _handler.Statuses.Enqueue(503);
        _clip.Current = new ClipboardContent.Text("a");
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal(1, _queue.Count);
        Assert.Empty(_notes.Messages);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(502)]
    [InlineData(429)]
    [InlineData(408)]
    public async Task Flush_hitting_transient_status_keeps_all_items_and_stops(int status)
    {
        await StartAsync();
        _queue.Enqueue(new ClipboardContent.Text("a"), _time.GetUtcNow());
        _queue.Enqueue(new ClipboardContent.Text("b"), _time.GetUtcNow());
        _handler.Statuses.Enqueue(status);
        await _c.FlushQueueAsync(default);
        Assert.Equal(2, _queue.Count);
        Assert.Empty(_notes.Messages);
        Assert.Empty(await ServerClipsAsync());
    }

    [Fact]
    public async Task Flush_413_drops_one_item_with_one_notification_and_continues()
    {
        await StartAsync();
        _queue.Enqueue(new ClipboardContent.Text("big"), _time.GetUtcNow());
        _queue.Enqueue(new ClipboardContent.Text("ok"), _time.GetUtcNow());
        _handler.Statuses.Enqueue(413);
        await _c.FlushQueueAsync(default);
        Assert.Equal(0, _queue.Count);
        Assert.Single(_notes.Messages);
        Assert.Equal("ok", Assert.Single(await ServerClipsAsync()).Text);
    }

    [Fact]
    public async Task Duplicate_changed_events_for_unchanged_content_upload_once()
    {
        await StartAsync();
        _clip.Current = new ClipboardContent.Text("same");
        await _c.OnClipboardChangedAsync(default);
        await _c.OnClipboardChangedAsync(default);
        Assert.Single(await ServerClipsAsync());
        Assert.Single(_handler.Uploads);
    }

    [Fact]
    public async Task Content_copied_while_paused_is_not_sent_after_resume()
    {
        await StartAsync();
        _paused = true;
        _clip.Current = new ClipboardContent.Text("private");
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal(0, _clip.Reads);   // paused: returns without reading
        _paused = false;
        await _c.OnResumedAsync(default);
        await _c.OnClipboardChangedAsync(default);   // spurious event, same content
        Assert.Empty(await ServerClipsAsync());

        _clip.Current = new ClipboardContent.Text("new");
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal("new", Assert.Single(await ServerClipsAsync()).Text);
    }

    [Fact]
    public async Task Write_failure_does_not_swallow_the_next_user_copy()
    {
        await StartAsync();
        _clip.WriteFailure = new InvalidOperationException("clipboard busy");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _c.ApplyRemoteAsync(RemoteText("x"), default));
        Assert.Null(_cache.LastAppliedAt);

        _clip.WriteFailure = null;
        _clip.Current = new ClipboardContent.Text("x");
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal("x", Assert.Single(await ServerClipsAsync()).Text);
    }

    [Fact]
    public async Task First_change_after_remote_write_is_own_even_if_os_transformed_it()
    {
        await StartAsync();
        var up = await _api.UploadImageAsync(TestImages.MakePng(6, 6, 1).ToArray(), null, false);
        await _c.ApplyRemoteAsync(up, default);
        _clip.ReadOverride = new ClipboardContent.Image(TestImages.MakePng(6, 6, 99).ToArray());   // different pixels, like a DIB round trip
        await _c.OnClipboardChangedAsync(default);
        Assert.Single(await ServerClipsAsync());   // only the original upload
    }

    [Fact]
    public async Task Own_write_guard_expires_after_one_second()
    {
        await StartAsync();
        await _c.ApplyRemoteAsync(RemoteText("x"), default);
        _time.Advance(TimeSpan.FromSeconds(2));
        _clip.Current = new ClipboardContent.Text("user copy");
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal("user copy", Assert.Single(await ServerClipsAsync()).Text);
    }

    [Fact]
    public async Task Flush_and_apply_remote_do_nothing_while_paused()
    {
        await StartAsync();
        _queue.Enqueue(new ClipboardContent.Text("q"), _time.GetUtcNow());
        _paused = true;
        await _c.FlushQueueAsync(default);
        Assert.Equal(1, _queue.Count);
        Assert.Empty(await ServerClipsAsync());

        await _c.ApplyRemoteAsync(RemoteText("x"), default);
        Assert.Empty(_clip.Writes);
        Assert.Null(_cache.LastAppliedAt);
    }

    [Fact]
    public async Task Copies_made_during_a_slow_upload_are_each_uploaded_in_order()
    {
        await StartAsync();
        var release = new TaskCompletionSource();
        _handler.Hold = release.Task;
        _clip.Current = new ClipboardContent.Text("one");
        var t1 = _c.OnClipboardChangedAsync(default);
        _clip.Current = new ClipboardContent.Text("two");
        var t2 = _c.OnClipboardChangedAsync(default);
        _clip.Current = new ClipboardContent.Text("three");
        var t3 = _c.OnClipboardChangedAsync(default);
        release.SetResult();
        await Task.WhenAll(t1, t2, t3);
        var texts = (await ServerClipsAsync()).OrderBy(c => c.Seq).Select(c => c.Text);
        Assert.Equal(new[] { "one", "two", "three" }, texts);
    }

    [Fact]
    public async Task Oversized_undecodable_image_notifies_once()
    {
        await StartAsync();
        _clip.Current = new ClipboardContent.Image(new byte[ClipboardCoordinator.MaxImageBytes + 1]);
        await _c.OnClipboardChangedAsync(default);
        Assert.Contains("too large", Assert.Single(_notes.Messages));
    }

    [Fact]
    public async Task Cache_save_failure_during_flush_is_non_fatal_and_item_is_dequeued()
    {
        await StartAsync();
        var bad = Path.Combine(_dir, "h-is-a-dir");
        Directory.CreateDirectory(bad);   // File.Move onto a directory fails
        var c = new ClipboardCoordinator(_clip, _api, new HistoryCache(bad, _key), _queue, _echo, _notes, _time, () => false);
        _queue.Enqueue(new ClipboardContent.Text("a"), _time.GetUtcNow());
        _queue.Enqueue(new ClipboardContent.Text("b"), _time.GetUtcNow());
        await c.FlushQueueAsync(default);
        Assert.Equal(0, _queue.Count);
        Assert.Equal(2, (await ServerClipsAsync()).Count);
    }
}
