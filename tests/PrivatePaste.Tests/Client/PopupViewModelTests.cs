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
}
