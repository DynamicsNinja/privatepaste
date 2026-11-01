using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Desktop.ViewModels;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Client;

public class PopupViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<string> _olderQueries = [];
    private IReadOnlyList<ClipDto> _olderResults = [];
    private Exception? _olderFails;
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

    /// <summary>Stands in for <c>OlderClipSearch</c>: records the query, then yields the results (or fails after recording).</summary>
    private async IAsyncEnumerable<ClipDto> Older(string query, [EnumeratorCancellation] CancellationToken ct)
    {
        _olderQueries.Add(query);
        await Task.CompletedTask;
        if (_olderFails is { } e) throw e;
        foreach (var clip in _olderResults) yield return clip;
    }

    private PopupViewModel Create(HistoryCache cache) => new(
        cache,
        Older,
        clip => { _activated.Add(clip); return Task.CompletedTask; },
        _time,
        notify: _toasts.Add);

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
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(_olderQueries);                           // typing never asks the server (it can't search)
        Assert.DoesNotContain(vm.Items, i => i.IsSearchOlder); // there are hits here
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
    public async Task Popup_SearchOlderRowAppears()
    {
        var vm = Create(CacheWith(Text("beta", 1, 2)));
        vm.Query = "alpha";

        var row = Assert.Single(vm.Items);
        Assert.True(row.IsSearchOlder);
        Assert.False(row.IsUnknown);
        Assert.Equal("Search older clips", row.Preview);
        Assert.Equal(0, vm.SelectedIndex);
        Assert.Empty(_olderQueries);   // only when asked

        var old = Text("alpha from last year", 500_000, 1);
        _olderResults = [old, Text("alpha older still", 600_000, 0), old];
        await vm.ActivateSelectedAsync();   // Enter on the row

        Assert.Equal(["alpha"], _olderQueries);
        Assert.Empty(_activated);           // nothing was pasted
        Assert.Equal(["alpha from last year", "alpha older still"], vm.Items.Select(i => i.Clip.Text));
        Assert.False(vm.IsSearchingOlder);
        Assert.Equal(0, vm.SelectedIndex);
        await vm.ActivateSelectedAsync();   // now a hit is pasted
        Assert.Equal([old], _activated);
    }

    [Fact]
    public async Task Search_older_with_no_hits_leaves_an_empty_list_and_a_new_query_offers_it_again()
    {
        var vm = Create(CacheWith(Text("beta", 1, 2)));
        vm.Query = "alpha";
        await vm.ActivateSelectedAsync();
        Assert.Empty(vm.Items);              // "No matches.": the search ran to the end
        vm.RefreshFromCache();
        Assert.Empty(vm.Items);

        vm.Query = "alph";
        Assert.True(Assert.Single(vm.Items).IsSearchOlder);
    }

    [Fact]
    public async Task Search_older_failure_keeps_what_was_found_and_offers_the_row_again()
    {
        var vm = Create(CacheWith(Text("beta", 1, 2)));
        _olderFails = new HttpRequestException("offline");
        vm.Query = "alpha";

        await vm.ActivateSelectedAsync();

        Assert.Equal(["Can't reach the server."], _toasts);
        Assert.True(Assert.Single(vm.Items).IsSearchOlder);
    }

    [Fact]
    public void No_search_older_row_without_a_query_on_pinned_or_when_locked()
    {
        var vm = Create(CacheWith());
        Assert.Empty(vm.Items);              // empty query: "Nothing to paste yet."
        vm.SwitchTab();
        vm.Query = "alpha";
        Assert.Empty(vm.Items);              // the Pinned tab lists the server's pins instead

        var locked = new PopupViewModel(CacheWith(), null, _ => Task.CompletedTask, _time) { Query = "alpha" };
        Assert.Empty(locked.Items);
    }

    [Fact]
    public async Task Popup_UnknownRowIsInert()
    {
        var unknown = new ClipDto(Guid.NewGuid(), 2, ClipKind.Unknown, null, "h", null, null, 10, null, "Phone", Now.AddMinutes(-1));
        var vm = Create(CacheWith(Text("plain", 5, 1), unknown));

        var row = vm.Items[0];
        Assert.True(row.IsUnknown);
        Assert.False(row.IsText || row.IsLink || row.IsImage || row.IsSearchOlder);
        Assert.Equal("Update PrivatePaste to see this clip", row.Preview);
        Assert.Equal("Phone, 1 min ago", row.Meta);

        await vm.ActivateSelectedAsync();   // Enter
        Assert.Empty(_activated);
        vm.MoveSelection(1);
        await vm.ActivateSelectedAsync();
        Assert.Equal("plain", Assert.Single(_activated).Text);
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
    public async Task Refresh_with_a_query_reruns_the_filter_and_keeps_older_hits()
    {
        var cache = CacheWith(Text("beta", 2, 2));
        var vm = Create(cache);
        _olderResults = [Text("alpha on server", 500, 0)];
        vm.Query = "alpha";
        await vm.ActivateSelectedAsync();
        cache.Upsert(Text("alpha two", 1, 3));
        cache.Upsert(Text("gamma", 0, 4));

        vm.RefreshFromCache();

        Assert.Equal("alpha", vm.Query);
        Assert.Equal(["alpha two", "alpha on server"], vm.Items.Select(i => i.Clip.Text));
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
        Older,
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
        var vm = new PopupViewModel(CacheWith(Pin(Text("p", 1, 1), 1)), Older,
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

    [Fact]
    public void PopupItem_PersonFromSourceUserName()
    {
        var withPerson = Text("a", 1, 1) with { SourceUserName = "Ana" };
        var cache = CacheWith(withPerson, Text("b", 2, 2));
        var vm = Create(cache);
        vm.Query = "";
        Assert.Equal("Ana", vm.Items.Single(i => i.Clip.Text == "a").Person);
        Assert.Null(vm.Items.Single(i => i.Clip.Text == "b").Person);
    }
}
