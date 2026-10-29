using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Desktop.ViewModels;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Client;

public class PopupViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<string> _serverQueries = [];
    private IReadOnlyList<ClipDto> _serverResults = [];
    private readonly List<ClipDto> _activated = [];

    private static ClipDto Text(string text, int minutesAgo, long seq, string device = "PC-work", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), seq, ClipKind.Text, text, text, null, null, text.Length, null, device, Now.AddMinutes(-minutesAgo));

    private static ClipDto Image(int w, int h, int minutesAgo, long seq) =>
        new(Guid.NewGuid(), seq, ClipKind.Image, null, "imghash" + seq, w, h, 1000, null, "PC-work", Now.AddMinutes(-minutesAgo));

    private static HistoryCache CacheWith(params ClipDto[] clips)
    {
        var cache = new HistoryCache(Path.Combine(Path.GetTempPath(), "pp-popup-" + Guid.NewGuid() + ".dat"), new byte[32]);
        foreach (var c in clips) cache.Upsert(c);
        return cache;
    }

    private PopupViewModel Create(HistoryCache cache) => new(
        cache,
        (q, _) => { _serverQueries.Add(q); return Task.FromResult(_serverResults); },
        clip => { _activated.Add(clip); return Task.CompletedTask; },
        _time);

    [Fact]
    public void Empty_query_shows_cache_newest_first()
    {
        var vm = Create(CacheWith(Text("old", 30, 1), Text("new", 1, 3), Text("mid", 10, 2)));
        Assert.Equal(["new", "mid", "old"], vm.Items.Select(i => i.Clip.Text));
        Assert.True(vm.Items[0].IsNewest);
        Assert.False(vm.Items[1].IsNewest);
    }

    [Fact]
    public void Typing_filters_cache_immediately()
    {
        var vm = Create(CacheWith(Text("alpha one", 3, 1), Text("beta", 2, 2), Text("alpha two", 1, 3)));
        vm.Query = "alpha";
        Assert.Equal(["alpha two", "alpha one"], vm.Items.Select(i => i.Clip.Text));
        Assert.All(vm.Items, i => Assert.False(i.IsNewest));   // highlight only while the query is empty
        Assert.Empty(_serverQueries);                          // server search is debounced
    }

    [Fact]
    public void Typed_text_appends_to_the_query_and_filters()
    {
        var vm = Create(CacheWith(Text("alpha", 2, 1), Text("beta", 1, 2)));
        vm.Type("al");
        vm.Type("p");
        Assert.Equal("alp", vm.Query);
        Assert.Equal(["alpha"], vm.Items.Select(i => i.Clip.Text));
    }

    [Fact]
    public void Backspace_removes_one_whole_character()
    {
        var vm = Create(CacheWith());
        vm.Type("ač🚀");
        vm.Backspace();
        Assert.Equal("ač", vm.Query);   // the emoji (a surrogate pair) goes in one step
        vm.Backspace();
        vm.Backspace();
        vm.Backspace();                 // on an empty query: nothing happens
        Assert.Equal("", vm.Query);
    }

    [Fact]
    public void Server_results_merge_after_cached_without_duplicates()
    {
        var cached = Text("alpha cached", 1, 2);
        var vm = Create(CacheWith(cached));
        var onlyOnServer = Text("alpha server", 500, 1);
        _serverResults = [onlyOnServer, cached];
        vm.Query = "alpha";
        _time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(["alpha cached", "alpha server"], vm.Items.Select(i => i.Clip.Text));
        Assert.Equal(1, vm.Items.Count(i => i.Clip.Id == cached.Id));
    }

    [Fact]
    public void Fast_typing_debounces_server_calls()
    {
        var vm = Create(CacheWith(Text("x", 1, 1)));
        foreach (var q in new[] { "a", "al", "alp", "alph" })
        {
            vm.Query = q;
            _time.Advance(TimeSpan.FromMilliseconds(50));
        }
        Assert.Empty(_serverQueries);
        _time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(["alph"], _serverQueries);
    }

    [Fact]
    public void Server_failure_keeps_cached_hits()
    {
        var vm = new PopupViewModel(CacheWith(Text("alpha", 1, 1)), (_, _) => throw new HttpRequestException("offline"),
            _ => Task.CompletedTask, _time);
        vm.Query = "alpha";
        _time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Single(vm.Items);
    }

    [Fact]
    public void Selection_clamps_and_resets()
    {
        var vm = Create(CacheWith(Text("a1", 3, 1), Text("a2", 2, 2), Text("b", 1, 3)));
        Assert.Equal(0, vm.SelectedIndex);
        vm.MoveSelection(-1);
        Assert.Equal(0, vm.SelectedIndex);
        vm.MoveSelection(1); vm.MoveSelection(1); vm.MoveSelection(1);
        Assert.Equal(2, vm.SelectedIndex);
        vm.Query = "a";
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public async Task Activate_calls_activate_with_selected_clip()
    {
        var top = Text("top", 1, 2);
        var vm = Create(CacheWith(Text("low", 5, 1), top));
        await vm.ActivateSelectedAsync();
        Assert.Equal([top], _activated);
        vm.MoveSelection(1);
        await vm.ActivateSelectedAsync();
        Assert.Equal("low", _activated[1].Text);
    }

    [Fact]
    public async Task Activate_with_no_items_is_a_noop()
    {
        var vm = Create(CacheWith());
        await vm.ActivateSelectedAsync();
        Assert.Empty(_activated);
    }

    [Fact]
    public void Preview_and_meta_format()
    {
        var long130 = new string('x', 130);
        var vm = Create(CacheWith(
            Text("first line\r\nsecond line", 2, 3),
            Image(1280, 720, 5, 2),
            Text(long130, 120, 1, "Laptop")));
        Assert.Equal("first line", vm.Items[0].Preview);
        Assert.Equal("PC-work, 2 min ago", vm.Items[0].Meta);
        Assert.Equal("Image 1280×720", vm.Items[1].Preview);
        Assert.Equal(120, vm.Items[2].Preview.Length);
        Assert.Equal("Laptop, 2 h ago", vm.Items[2].Meta);
    }

    [Fact]
    public void Refresh_shows_new_history_and_keeps_the_selected_clip()
    {
        var cache = CacheWith(Text("old", 30, 1), Text("mid", 10, 2));
        var vm = Create(cache);
        vm.MoveSelection(1);   // "old"
        cache.Upsert(Text("arrived while open", 0, 3));

        vm.RefreshFromCache();

        Assert.Equal(["arrived while open", "mid", "old"], vm.Items.Select(i => i.Clip.Text));
        Assert.True(vm.Items[0].IsNewest);
        Assert.Equal("old", vm.Items[vm.SelectedIndex].Clip.Text);
    }

    [Fact]
    public void Refresh_with_a_query_reruns_the_filter_and_keeps_server_hits()
    {
        var cache = CacheWith(Text("alpha one", 3, 1), Text("beta", 2, 2));
        var vm = Create(cache);
        var serverOnly = Text("alpha on server", 500, 0);
        _serverResults = [serverOnly];
        vm.Query = "alpha";
        _time.Advance(TimeSpan.FromMilliseconds(250));
        cache.Upsert(Text("alpha two", 1, 3));
        cache.Upsert(Text("gamma", 0, 4));

        vm.RefreshFromCache();

        Assert.Equal("alpha", vm.Query);
        Assert.Equal(["alpha two", "alpha one", "alpha on server"], vm.Items.Select(i => i.Clip.Text));
    }

    [Fact]
    public void Refresh_picks_up_a_new_thumbnail()
    {
        var image = Image(10, 10, 1, 1);
        var cache = CacheWith(image);
        var vm = Create(cache);
        Assert.Null(vm.Items[0].Thumbnail);
        cache.SetThumbnail(image.Id, image.Hash, [1, 2, 3]);

        vm.RefreshFromCache();

        Assert.Equal(new byte[] { 1, 2, 3 }, vm.Items[0].Thumbnail);
    }

    // ---- Pinned tab and pins ----

    private readonly List<(Guid Id, bool Pinned)> _pinCalls = [];
    private readonly List<string> _toasts = [];
    private IReadOnlyList<ClipDto> _serverPins = [];
    private Exception? _pinFails;

    private static ClipDto Pin(ClipDto clip, int minutes) => clip with { PinnedAt = Now.AddMinutes(minutes) };

    private PopupViewModel CreatePins(HistoryCache cache) => new(
        cache,
        (q, _) => { _serverQueries.Add(q); return Task.FromResult(_serverResults); },
        clip => { _activated.Add(clip); return Task.CompletedTask; },
        _time,
        setPinned: (id, pinned) =>
        {
            _pinCalls.Add((id, pinned));
            if (_pinFails is not null) throw _pinFails;
            var held = cache.Items.First(i => i.Clip.Id == id).Clip;
            return Task.FromResult(held with { Seq = held.Seq + 100, PinnedAt = pinned ? Now : null });
        },
        serverPinned: (q, _) => Task.FromResult(_serverPins),
        notify: _toasts.Add);

    [Fact]
    public void Switching_tab_keeps_the_query_and_lists_pinned_newest_pinned_first()
    {
        var vm = CreatePins(CacheWith(
            Text("plain", 1, 5), Pin(Text("pin alpha", 30, 1), 10), Pin(Text("pin beta", 20, 2), 20), Text("other", 5, 3)));
        Assert.Equal(PopupTab.History, vm.Tab);
        Assert.Equal("Enter to paste   Tab for Pinned   Ctrl+P to pin", vm.FooterHint);

        vm.SwitchTab();
        Assert.Equal(PopupTab.Pinned, vm.Tab);
        Assert.Equal(["pin beta", "pin alpha"], vm.Items.Select(i => i.Clip.Text));
        Assert.All(vm.Items, i => Assert.True(i.IsPinned));
        Assert.All(vm.Items, i => Assert.False(i.IsNewest));
        Assert.Equal("Enter to paste   Tab for History   Ctrl+P to pin", vm.FooterHint);

        vm.Query = "alpha";   // search filters the open tab
        Assert.Equal(["pin alpha"], vm.Items.Select(i => i.Clip.Text));
        vm.SwitchTab();
        Assert.Equal("alpha", vm.Query);
        Assert.Equal(PopupTab.History, vm.Tab);
        Assert.Equal(["pin alpha"], vm.Items.Select(i => i.Clip.Text));
        Assert.True(vm.Items[0].IsPinned);
    }

    [Fact]
    public void Pinned_tab_adds_server_pins_the_cache_lost_without_duplicates()
    {
        var cached = Pin(Text("cached pin", 1, 2), 5);
        var vm = CreatePins(CacheWith(cached));
        _serverPins = [cached, Pin(Text("trimmed pin", 9000, 1), 1)];
        vm.SwitchTab();
        Assert.Equal(["cached pin", "trimmed pin"], vm.Items.Select(i => i.Clip.Text));
    }

    [Fact]
    public void Pinned_tab_survives_a_failing_server()
    {
        var vm = new PopupViewModel(CacheWith(Pin(Text("p", 1, 1), 1)), (_, _) => Task.FromResult<IReadOnlyList<ClipDto>>([]),
            _ => Task.CompletedTask, _time, serverPinned: (_, _) => throw new HttpRequestException("offline"));
        vm.SwitchTab();
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task Toggling_the_pin_calls_the_api_and_updates_the_row_and_cache()
    {
        var clip = Text("hello", 1, 1);
        var cache = CacheWith(clip);
        var vm = CreatePins(cache);
        await vm.TogglePinSelectedAsync();
        Assert.Equal([(clip.Id, true)], _pinCalls);
        Assert.True(vm.Items[0].IsPinned);
        Assert.Single(cache.Pinned);
        await vm.TogglePinSelectedAsync();
        Assert.Equal((clip.Id, false), _pinCalls[1]);
        Assert.False(vm.Items[0].IsPinned);
        Assert.Empty(cache.Pinned);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Unpinning_on_the_pinned_tab_removes_the_row_and_keeps_the_selection_in_range()
    {
        var vm = CreatePins(CacheWith(Pin(Text("a", 2, 1), 2), Pin(Text("b", 1, 2), 1)));
        vm.SwitchTab();   // b (pinned later... a pinned at +2 so a first)
        vm.MoveSelection(1);
        await vm.TogglePinSelectedAsync();
        Assert.Equal(["a"], vm.Items.Select(i => i.Clip.Text));
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public async Task A_failed_pin_toasts_plain_copy_and_changes_nothing()
    {
        var vm = CreatePins(CacheWith(Text("hello", 1, 1)));
        _pinFails = new HttpRequestException("offline");
        await vm.TogglePinSelectedAsync();
        Assert.False(vm.Items[0].IsPinned);
        Assert.Equal(["Couldn't pin the clip."], _toasts);
    }

    [Fact]
    public async Task A_failed_unpin_says_unpin()
    {
        var vm = CreatePins(CacheWith(Pin(Text("hello", 1, 1), 1)));
        _pinFails = new HttpRequestException("offline");
        await vm.TogglePinSelectedAsync();
        Assert.True(vm.Items[0].IsPinned);
        Assert.Equal(["Couldn't unpin the clip."], _toasts);
    }

    [Fact]
    public async Task Toggling_with_no_items_is_a_noop()
    {
        var vm = CreatePins(CacheWith());
        await vm.TogglePinSelectedAsync();
        Assert.Empty(_pinCalls);
    }

    [Fact]
    public async Task Refresh_on_the_pinned_tab_keeps_server_only_pins()
    {
        var cache = CacheWith(Pin(Text("cached pin", 1, 2), 5));
        var vm = CreatePins(cache);
        _serverPins = [Pin(Text("trimmed pin", 9000, 1), 1)];
        vm.SwitchTab();
        cache.Upsert(Pin(Text("new pin", 0, 3), 9));
        vm.RefreshFromCache();
        Assert.Equal(["new pin", "cached pin", "trimmed pin"], vm.Items.Select(i => i.Clip.Text));
        await Task.CompletedTask;
    }
}
