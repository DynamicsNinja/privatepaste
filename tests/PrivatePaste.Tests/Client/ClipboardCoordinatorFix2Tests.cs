using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Images;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Tests.Images;

namespace PrivatePaste.Tests.Client;

public partial class ClipboardCoordinatorTests
{
    [Fact]
    public async Task Copy_read_before_a_remote_write_is_not_mistaken_for_the_own_write()
    {
        await StartAsync();
        var remoteImage = await _api.UploadImageAsync(TestImages.MakePng(5, 5, 1).ToArray(), null, false);
        var mine = TestImages.MakePng(5, 5, 2).ToArray();
        var release = new TaskCompletionSource();
        _handler.Hold = release.Task;

        _clip.Current = new ClipboardContent.Text("slow");
        var t1 = _c.OnClipboardChangedAsync(default);                 // holds the gate in a slow upload
        var apply = _c.ApplyRemoteAsync(remoteImage, default);        // queued first at the gate
        await Task.Delay(100);                                        // let the apply fetch the image and queue at the gate
        _clip.Current = new ClipboardContent.Image(mine);
        var t2 = _c.OnClipboardChangedAsync(default);                 // user copy A: read now, handled after the remote write
        _time.Advance(TimeSpan.FromMilliseconds(1));
        release.SetResult();
        await Task.WhenAll(t1, apply, t2);

        var hashes = (await ServerClipsAsync()).Select(c => c.Hash).ToList();
        Assert.Contains(ImageHashing.HashPng(mine), hashes);
    }

    [Fact]
    public async Task Text_copy_right_after_a_remote_image_write_is_uploaded()
    {
        await StartAsync();
        var up = await _api.UploadImageAsync(TestImages.MakePng(5, 5, 1).ToArray(), null, false);
        await _c.ApplyRemoteAsync(up, default);
        _clip.Current = new ClipboardContent.Text("typed text");
        await _c.OnClipboardChangedAsync(default);
        Assert.Contains("typed text", (await ServerClipsAsync()).Select(c => c.Text));
    }

    [Fact]
    public async Task Same_oversized_content_notifies_once()
    {
        await StartAsync();
        _clip.Current = new ClipboardContent.Text(new string('x', 1_048_577));
        await _c.OnClipboardChangedAsync(default);
        await _c.OnClipboardChangedAsync(default);
        Assert.Single(_notes.Messages);

        _clip.Current = new ClipboardContent.Text(new string('y', 1_048_577));
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal(2, _notes.Messages.Count);
    }

    [Fact]
    public async Task Oversized_content_copied_while_paused_does_not_notify_after_resume()
    {
        await StartAsync();
        _paused = true;
        _clip.Current = new ClipboardContent.Text(new string('x', 1_048_577));
        await _c.OnResumedAsync(default);
        _paused = false;
        await _c.OnClipboardChangedAsync(default);
        Assert.Empty(_notes.Messages);
    }

    [Fact]
    public async Task Save_failure_notifies_once_and_latch_resets_after_a_successful_save()
    {
        await StartAsync();
        var path = Path.Combine(_dir, "h-latch");
        Directory.CreateDirectory(path);   // File.Move onto a directory fails
        var c = new ClipboardCoordinator(_clip, _api, new HistoryCache(path, _key), _queue, _echo, _notes, _time, () => false);
        _queue.Enqueue(new ClipboardContent.Text("a"), _time.GetUtcNow());
        _queue.Enqueue(new ClipboardContent.Text("b"), _time.GetUtcNow());
        await c.FlushQueueAsync(default);
        Assert.Single(_notes.Messages);

        Directory.Delete(path);   // saving works again
        _queue.Enqueue(new ClipboardContent.Text("c"), _time.GetUtcNow());
        await c.FlushQueueAsync(default);
        Assert.Single(_notes.Messages);

        File.Delete(path);
        Directory.CreateDirectory(path);   // fails again: must notify again
        _queue.Enqueue(new ClipboardContent.Text("d"), _time.GetUtcNow());
        await c.FlushQueueAsync(default);
        Assert.Equal(2, _notes.Messages.Count);
    }

    [Fact]
    public async Task Same_content_is_uploaded_after_a_401_and_re_sign_in()
    {
        await StartAsync();
        var good = _api.Token;
        _api.Token = "bogus";
        _clip.Current = new ClipboardContent.Text("keep me");
        await Assert.ThrowsAsync<PrivatePaste.Client.Core.Api.UnauthorizedApiException>(() => _c.OnClipboardChangedAsync(default));
        _api.Token = good;
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal("keep me", Assert.Single(await ServerClipsAsync()).Text);
    }

    [Fact]
    public async Task Rejected_content_is_not_remembered_so_a_retry_is_attempted()
    {
        await StartAsync();
        _handler.Statuses.Enqueue(413);
        _clip.Current = new ClipboardContent.Text("retry");
        await _c.OnClipboardChangedAsync(default);
        Assert.Single(_notes.Messages);
        await _c.OnClipboardChangedAsync(default);
        Assert.Equal("retry", Assert.Single(await ServerClipsAsync()).Text);
    }

    /// <summary>Queues the given texts while offline, then goes back online with "poison" uploads failing transiently.</summary>
    private async Task QueueWithPoisonAsync(params string[] texts)
    {
        _handler.Offline = true;
        foreach (var t in texts)
        {
            _clip.Current = new ClipboardContent.Text(t);
            await _c.OnClipboardChangedAsync(default);
        }
        Assert.Equal(texts.Length, _queue.Count);
        _handler.Offline = false;
        _handler.FailTextUpload = body => body.Contains("poison");
    }

    [Fact]
    public async Task A_poison_item_rotates_to_the_back_and_later_items_still_upload()
    {
        await StartAsync();
        await QueueWithPoisonAsync("poison", "good");

        for (var i = 0; i < 4; i++) await _c.FlushQueueAsync(default);
        Assert.Equal(2, _queue.Count);   // four failures: still at the head, the flush stops there
        Assert.DoesNotContain("good", (await ServerClipsAsync()).Select(c => c.Text));

        await _c.FlushQueueAsync(default);   // fifth failure: moved to the back, "good" goes next
        Assert.Contains("good", (await ServerClipsAsync()).Select(c => c.Text));
        Assert.Equal(1, _queue.Count);
        Assert.True(_queue.TryPeek(out var head));
        Assert.Equal(new ClipboardContent.Text("poison"), head.Content);
        Assert.Empty(_notes.Messages);
    }

    [Fact]
    public async Task A_poison_item_is_dropped_after_ten_tries_with_exactly_one_toast()
    {
        await StartAsync();
        await QueueWithPoisonAsync("poison");

        for (var i = 0; i < 9; i++) await _c.FlushQueueAsync(default);
        Assert.Equal(1, _queue.Count);
        Assert.Empty(_notes.Messages);

        await _c.FlushQueueAsync(default);
        Assert.Equal(0, _queue.Count);
        for (var i = 0; i < 3; i++) await _c.FlushQueueAsync(default);
        Assert.Equal(["Couldn't sync a clip after several tries."], _notes.Messages);
        Assert.Equal(0, OutboundQueue.Load(Path.Combine(_dir, "q.enc"), _key).Count);   // the drop was persisted
    }

    [Fact]
    public void Attempts_survive_a_restart()
    {
        var path = Path.Combine(_dir, "attempts.enc");
        var q = new OutboundQueue(path, _key);
        q.Enqueue(new ClipboardContent.Text("a"), DateTimeOffset.UnixEpoch);
        Assert.Equal(1, q.RecordFailure());
        Assert.Equal(2, q.RecordFailure());
        Assert.True(OutboundQueue.Load(path, _key).TryPeek(out var item));
        Assert.Equal(2, item.Attempts);
    }
}
