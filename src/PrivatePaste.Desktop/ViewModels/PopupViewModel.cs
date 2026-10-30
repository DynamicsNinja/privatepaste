using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Desktop.Theme;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop.ViewModels;

public enum PopupTab { History, Pinned }

/// <summary>One popup row. <see cref="IsNewest"/> is true only for the first row while the query is empty.</summary>
public sealed record PopupItem(ClipDto Clip, string Preview, string Meta, byte[]? Thumbnail, bool IsNewest = false)
{
    /// <summary>Who copied it; null while the server has one person, so the row shows no chip.</summary>
    public string? Person => string.IsNullOrWhiteSpace(Clip.SourceUserName) ? null : Clip.SourceUserName.Trim();

    public bool IsImage => Clip.Kind == ClipKind.Image;
    public bool IsLink => Clip.Kind == ClipKind.Link;
    public bool IsText => Clip.Kind == ClipKind.Text;
    public bool IsPinned => Clip.PinnedAt is not null;

    /// <summary>Tooltip and screen-reader name of the row's pin button.</summary>
    public string PinLabel => IsPinned ? "Unpin" : "Pin";

    /// <summary>The icon shown before the source device name.</summary>
    public PlatformIcon Platform => PlatformIcons.For(Clip.SourceDevicePlatform);
}

/// <summary>
/// Search-as-you-type over the local history. Cached hits show immediately; after a 250 ms pause the server is searched
/// and its results are appended (de-duplicated by Id). No UI-thread dependency: the debounce continuation resumes on the
/// captured synchronization context, so on the UI thread it touches <see cref="Items"/> there, and in tests it runs inline.
/// </summary>
public sealed partial class PopupViewModel : ObservableObject
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    public const int PreviewLength = 120;
    private const int ServerLimit = 50;

    private readonly HistoryCache _cache;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ClipDto>>> _serverSearch;
    private readonly Func<ClipDto, Task> _activate;
    private readonly TimeProvider _time;
    private readonly Func<Guid, bool, Task<ClipDto>>? _setPinned;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ClipDto>>>? _serverPinned;
    private readonly Action<string>? _notify;
    private CancellationTokenSource? _search;

    public PopupViewModel(
        HistoryCache cache,
        Func<string, CancellationToken, Task<IReadOnlyList<ClipDto>>> serverSearch,
        Func<ClipDto, Task> activate,
        TimeProvider time,
        Func<Guid, bool, Task<ClipDto>>? setPinned = null,
        Func<string, CancellationToken, Task<IReadOnlyList<ClipDto>>>? serverPinned = null,
        Action<string>? notify = null)
    {
        _setPinned = setPinned;
        _serverPinned = serverPinned;
        _notify = notify;
        _cache = cache;
        _serverSearch = serverSearch;
        _activate = activate;
        _time = time;
        Reset(cache.Search(""), highlightNewest: true);
    }

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private int _selectedIndex;

    public ObservableCollection<PopupItem> Items { get; } = [];

    /// <summary>The open tab. Switching keeps the query: it filters whichever tab is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistoryTab), nameof(IsPinnedTab), nameof(FooterHint))]
    private PopupTab _tab = PopupTab.History;

    public bool IsHistoryTab => Tab == PopupTab.History;
    public bool IsPinnedTab => Tab == PopupTab.Pinned;

    public string FooterHint => $"Enter to paste   Tab for {(Tab == PopupTab.History ? "Pinned" : "History")}   Ctrl+P to pin";

    public void SwitchTab() => ShowTab(Tab == PopupTab.History ? PopupTab.Pinned : PopupTab.History);

    public void ShowTab(PopupTab tab)
    {
        if (Tab == tab) return;
        Tab = tab;
        Refilter();
    }

    partial void OnQueryChanged(string value) => Refilter();

    /// <summary>Lists the open tab's cached clips for the current query and starts the server lookup for it.</summary>
    private void Refilter()
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = null;

        var query = Query.Trim();
        Reset(CachedFor(query), highlightNewest: Tab == PopupTab.History && query.Length == 0);

        // History has nothing to ask the server until something is typed. The Pinned tab always asks: pins trimmed from the
        // cache before pins existed only live on the server.
        if (query.Length == 0 && Tab == PopupTab.History) return;
        _search = new CancellationTokenSource();
        _ = SearchServerAsync(query, Tab, _search.Token);
    }

    private IReadOnlyList<CachedClip> CachedFor(string query) =>
        Tab == PopupTab.History
            ? _cache.Search(query)
            : _cache.Pinned.Where(c => query.Length == 0
                || c.Clip.Text is { } t && t.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

    private async Task SearchServerAsync(string query, PopupTab tab, CancellationToken ct)
    {
        try
        {
            if (query.Length > 0) await Task.Delay(Debounce, _time, ct);
            var lookup = tab == PopupTab.History ? _serverSearch : _serverPinned;
            if (lookup is null) return;
            var results = await lookup(query, ct);
            if (ct.IsCancellationRequested) return;
            Merge(results);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Offline, signed out or a server error: the cached hits stay; there is nobody to tell while typing.
        }
    }

    private void Reset(IEnumerable<CachedClip> clips, bool highlightNewest)
    {
        Items.Clear();
        foreach (var c in clips) Items.Add(ToItem(c.Clip, c.ThumbnailPng, newest: highlightNewest && Items.Count == 0));
        SelectedIndex = 0;
    }

    /// <summary>Appends server hits not already listed. The selection is kept: appended rows sit below it.</summary>
    private void Merge(IReadOnlyList<ClipDto> results)
    {
        var known = Items.Select(i => i.Clip.Id).ToHashSet();
        foreach (var clip in results.Take(ServerLimit))
            if (known.Add(clip.Id)) Items.Add(ToItem(clip, null, newest: false));
    }

    private PopupItem ToItem(ClipDto clip, byte[]? thumbnail, bool newest) =>
        new(clip, FormatPreview(clip), $"{clip.SourceDeviceName}, {Ago(clip.LastCopiedAt)}", thumbnail, newest);

    public static string FormatPreview(ClipDto clip)
    {
        if (clip.Kind == ClipKind.Image) return $"Image {clip.Width}×{clip.Height}";
        var line = (clip.Text ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= PreviewLength ? line : line[..(PreviewLength - 1)] + "…";
    }

    /// <summary>Same wording as the web history ("just now", "2 min ago", "3 h ago", "4 d ago", then the date).</summary>
    private string Ago(DateTimeOffset at)
    {
        var span = _time.GetUtcNow() - at;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} d ago";
        return at.ToString("yyyy-MM-dd");
    }

    /// <summary>Moves the selection by delta, clamped to the list (it never wraps).</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            SelectedIndex = 0;
            return;
        }
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Items.Count - 1);
    }

    /// <summary>Text typed while the popup is open without the focus (Windows): appended to the search.</summary>
    public void Type(string text) => Query += text;

    /// <summary>Removes the last character of the search (a whole emoji or accented letter, not half of one).</summary>
    public void Backspace()
    {
        if (Query.Length == 0) return;
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(Query);
        Query = Query[..elements[^1]];
    }

    public Task TogglePinSelectedAsync() =>
        SelectedIndex >= 0 && SelectedIndex < Items.Count ? TogglePinAsync(Items[SelectedIndex]) : Task.CompletedTask;

    /// <summary>Pins or unpins a listed clip through the server, then updates the cache and the row. Never throws.</summary>
    public async Task TogglePinAsync(PopupItem item)
    {
        if (_setPinned is null) return;
        var pin = !item.IsPinned;
        ClipDto updated;
        try { updated = await _setPinned(item.Clip.Id, pin); }
        catch (Exception)
        {
            _notify?.Invoke(pin ? "Couldn't pin the clip." : "Couldn't unpin the clip.");
            return;
        }

        // A clip the cache does not hold (a server hit) joins it only when pinned: pins outlive the cache capacity.
        if (updated.PinnedAt is not null || _cache.Items.Any(c => c.Clip.Id == updated.Id))
            _cache.Upsert(updated, fromSync: false);

        var index = IndexOf(updated.Id);
        if (index < 0) return;
        if (Tab == PopupTab.Pinned && updated.PinnedAt is null)
        {
            Items.RemoveAt(index);
            SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, Items.Count - 1));
        }
        else
        {
            var selected = SelectedIndex;
            Items[index] = ToItem(updated, Items[index].Thumbnail, Items[index].IsNewest);
            // Replacing a row can drop the list's selection; put it back on the same row.
            SelectedIndex = -1;
            SelectedIndex = selected;
        }
    }

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < Items.Count; i++)
            if (Items[i].Clip.Id == id) return i;
        return -1;
    }

    public Task ActivateSelectedAsync() =>
        SelectedIndex >= 0 && SelectedIndex < Items.Count ? _activate(Items[SelectedIndex].Clip) : Task.CompletedTask;

    /// <summary>
    /// The history changed while the popup is open: re-runs the current filter over the cache, keeps server hits that the
    /// cache does not hold, and keeps the selection on the same clip (or the same row if that clip is gone). Call on the
    /// thread that owns <see cref="Items"/>.
    /// </summary>
    public void RefreshFromCache()
    {
        var query = Query.Trim();
        var pinnedTab = Tab == PopupTab.Pinned;
        var selectedId = SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex].Clip.Id : (Guid?)null;
        var previousIndex = SelectedIndex;
        var held = _cache.Items.Select(c => c.Clip.Id).ToHashSet();
        var serverHits = query.Length == 0 && !pinnedTab ? [] : Items.Where(i => !held.Contains(i.Clip.Id)).Select(i => i.Clip).ToList();
        var cached = CachedFor(query);

        Reset(cached, highlightNewest: !pinnedTab && query.Length == 0);
        foreach (var clip in serverHits) Items.Add(ToItem(clip, null, newest: false));

        var index = selectedId is { } id ? Items.Select(i => i.Clip.Id).ToList().IndexOf(id) : -1;
        SelectedIndex = index >= 0 ? index : Math.Clamp(previousIndex, 0, Math.Max(0, Items.Count - 1));
    }

    /// <summary>Stops a pending server search (the window closed).</summary>
    public void Cancel()
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
    }
}
