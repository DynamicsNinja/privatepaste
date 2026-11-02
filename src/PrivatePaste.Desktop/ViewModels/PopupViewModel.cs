using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Desktop.Theme;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop.ViewModels;

public enum PopupTab { History, Pinned }

/// <summary>The type chips under the tabs, in their order (Ctrl+1 to Ctrl+5). One at a time; clips of an unknown kind show under All only.</summary>
public enum PopupFilter { All, Text, Link, Image, File }

/// <summary>A server lookup that needs the vault key was asked for while locked or signed out.</summary>
public sealed class VaultLockedException() : InvalidOperationException("Locked or signed out.");

/// <summary>
/// One popup row. <see cref="IsNewest"/> is true only for the first row while the query is empty. The
/// <see cref="SearchOlder"/> row is not a clip: Enter on it searches the server's older history.
/// </summary>
public sealed record PopupItem(ClipDto Clip, string Preview, string Meta, byte[]? Thumbnail, bool IsNewest = false)
{
    public const string UnknownText = "Update PrivatePaste to see this clip";
    public const string SearchOlderText = "Search older clips";

    /// <summary>The last row of a search with no local hits.</summary>
    public static readonly PopupItem SearchOlder = new(
        new ClipDto(Guid.Empty, 0, ClipKind.Unknown, null, "", null, null, 0, null, "", default), SearchOlderText, "", null)
    {
        IsSearchOlder = true,
    };

    public bool IsSearchOlder { get; private init; }

    /// <summary>A clip this app can't show (a newer app's kind, or one that doesn't open): shown, never pasted.</summary>
    public bool IsUnknown => Clip.Kind == ClipKind.Unknown && !IsSearchOlder;

    /// <summary>Real clips have a device line and a pin button; the search row has neither.</summary>
    public bool IsClip => !IsSearchOlder;
    /// <summary>Who copied it; null while the server has one person, so the row shows no chip.</summary>
    public string? Person => string.IsNullOrWhiteSpace(Clip.SourceUserName) ? null : Clip.SourceUserName.Trim();

    public bool IsImage => Clip.Kind == ClipKind.Image;
    public bool IsLink => Clip.Kind == ClipKind.Link;
    public bool IsText => Clip.Kind == ClipKind.Text;
    public bool IsFile => Clip.Kind == ClipKind.File;
    public bool IsPinned => Clip.PinnedAt is not null;

    /// <summary>Tooltip and screen-reader name of the row's pin button.</summary>
    public string PinLabel => IsPinned ? "Unpin" : "Pin";

    /// <summary>The icon shown before the source device name.</summary>
    public PlatformIcon Platform => PlatformIcons.For(Clip.SourceDevicePlatform);
}

/// <summary>
/// Search-as-you-type over the local history. The server holds only ciphertext and can't search, so typing never asks it:
/// when nothing here matches, a last row offers "Search older clips", and Enter on it pages through the server's history
/// on this device (<c>searchOlder</c>), appending the matches (de-duplicated by Id) as they are found. The Pinned tab
/// also lists the server's pins (after a 250 ms pause while typing). No UI-thread dependency: continuations resume on the
/// captured synchronization context, so on the UI thread they touch <see cref="Items"/> there, and in tests run inline.
/// </summary>
public sealed partial class PopupViewModel : ObservableObject
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    public const int PreviewLength = 120;
    private const int ServerLimit = 50;
    public const string SearchingOlderText = "Searching older clips…";
    public const string UnreachableText = "Can't reach the server.";
    public const string CouldNotSaveText = "Couldn't save the files.";

    private readonly HistoryCache _cache;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<ClipDto>>? _searchOlder;
    private readonly Func<ClipDto, Task> _activate;
    private readonly TimeProvider _time;
    private readonly Func<Guid, bool, Task<ClipDto>>? _setPinned;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ClipDto>>>? _serverPinned;
    private readonly Action<string>? _notify;
    private readonly Func<ClipDto, Task<string?>>? _save;
    private CancellationTokenSource? _search;
    /// <summary>The query an older-clip search ran for (or runs for), and whether it reached the end.</summary>
    private string? _olderQuery;
    private bool _olderDone;
    /// <summary>The vault locked (or the sign-in ended) under a search: no more older-clip searches in this popup.</summary>
    private bool _olderUnavailable;

    /// <param name="searchOlder">(query, ct) → the server's older clips that match, newest first; null: no such search.</param>
    /// <param name="save">Saves a file clip's files to Downloads and returns what to tell the user (null: nothing).</param>
    public PopupViewModel(
        HistoryCache cache,
        Func<string, CancellationToken, IAsyncEnumerable<ClipDto>>? searchOlder,
        Func<ClipDto, Task> activate,
        TimeProvider time,
        Func<Guid, bool, Task<ClipDto>>? setPinned = null,
        Func<string, CancellationToken, Task<IReadOnlyList<ClipDto>>>? serverPinned = null,
        Action<string>? notify = null,
        Func<ClipDto, Task<string?>>? save = null)
    {
        _setPinned = setPinned;
        _serverPinned = serverPinned;
        _notify = notify;
        _save = save;
        _cache = cache;
        _searchOlder = searchOlder;
        _activate = activate;
        _time = time;
        // The footer follows the selected row, which can change kind under the same index when the list changes.
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FooterHint));
        Reset(cache.Search(""), highlightNewest: true);
    }

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterHint))]
    private int _selectedIndex;

    public ObservableCollection<PopupItem> Items { get; } = [];

    /// <summary>A line above the list while sync can't run (e.g. "Update PrivatePaste to keep syncing."); null for none.</summary>
    [ObservableProperty]
    private string? _banner;

    /// <summary>True while "Search older clips" pages through the server.</summary>
    [ObservableProperty]
    private bool _isSearchingOlder;

    /// <summary>The open tab. Switching keeps the query: it filters whichever tab is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistoryTab), nameof(IsPinnedTab), nameof(FooterHint))]
    private PopupTab _tab = PopupTab.History;

    public bool IsHistoryTab => Tab == PopupTab.History;
    public bool IsPinnedTab => Tab == PopupTab.Pinned;

    /// <summary>The type chip in force; it starts at All each time the popup opens (a new view model per opening) and survives tab switches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllFilter), nameof(IsTextFilter), nameof(IsLinkFilter), nameof(IsImageFilter), nameof(IsFileFilter))]
    private PopupFilter _filter = PopupFilter.All;

    public bool IsAllFilter => Filter == PopupFilter.All;
    public bool IsTextFilter => Filter == PopupFilter.Text;
    public bool IsLinkFilter => Filter == PopupFilter.Link;
    public bool IsImageFilter => Filter == PopupFilter.Image;
    public bool IsFileFilter => Filter == PopupFilter.File;

    /// <summary>A chip click or Ctrl+digit: picks the type, or returns to All when it is already the active one (or is All).</summary>
    public void SetFilter(PopupFilter filter)
    {
        if (filter == Filter) filter = PopupFilter.All;
        if (filter == Filter) return;
        Filter = filter;
        Refilter();
    }

    /// <summary>Ctrl+1 to Ctrl+5, the chips in order; anything else is ignored.</summary>
    public void SetFilterByIndex(int index)
    {
        if (index is >= 1 and <= 5) SetFilter((PopupFilter)(index - 1));
    }

    private bool PassesFilter(ClipDto clip) => Filter switch
    {
        PopupFilter.Text => clip.Kind == ClipKind.Text,
        PopupFilter.Link => clip.Kind == ClipKind.Link,
        PopupFilter.Image => clip.Kind == ClipKind.Image,
        PopupFilter.File => clip.Kind == ClipKind.File,
        _ => true,
    };

    /// <summary>What the list says when it has no rows (and no search row).</summary>
    public string EmptyText
    {
        get
        {
            if (IsSearchingOlder) return SearchingOlderText;
            if (Query.Trim().Length > 0) return "No matches.";
            var pinned = Tab == PopupTab.Pinned;
            return Filter switch
            {
                PopupFilter.Text => pinned ? "No pinned text" : "No text yet",
                PopupFilter.Link => pinned ? "No pinned links" : "No links yet",
                PopupFilter.Image => pinned ? "No pinned images" : "No images yet",
                PopupFilter.File => pinned ? "No pinned files" : "No files yet",
                _ => pinned ? "Nothing pinned yet." : "Nothing to paste yet.",
            };
        }
    }

    public string FooterHint => $"Enter to paste   Tab for {(Tab == PopupTab.History ? "Pinned" : "History")}   Ctrl+P to pin"
        + (Selected is { IsFile: true } ? "   Ctrl+S to save" : "")
        + "   Ctrl+1–5 to filter";

    private PopupItem? Selected => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;

    public void SwitchTab() => ShowTab(Tab == PopupTab.History ? PopupTab.Pinned : PopupTab.History);

    public void ShowTab(PopupTab tab)
    {
        if (Tab == tab) return;
        Tab = tab;
        Refilter();
    }

    partial void OnQueryChanged(string value) => Refilter();

    /// <summary>Lists the open tab's cached clips for the current query (and, on the Pinned tab, asks the server for its pins).</summary>
    private void Refilter()
    {
        Cancel();
        _olderQuery = null;
        _olderDone = false;
        IsSearchingOlder = false;

        var query = Query.Trim();
        Reset(CachedFor(query), highlightNewest: Tab == PopupTab.History && query.Length == 0);
        AddSearchOlderRow();

        // The Pinned tab always asks: pins trimmed from the cache before pins existed only live on the server.
        if (Tab != PopupTab.Pinned) return;
        _search = new CancellationTokenSource();
        _ = ListServerPinsAsync(query, _search.Token);
    }

    /// <summary>Offers "Search older clips" as the only row of a History search with no hits here, unless that search already ran.</summary>
    private void AddSearchOlderRow()
    {
        var query = Query.Trim();
        if (_searchOlder is null || _olderUnavailable || Tab != PopupTab.History || query.Length == 0 || Items.Count > 0) return;
        if (_olderQuery == query && (IsSearchingOlder || _olderDone)) return;
        Items.Add(PopupItem.SearchOlder);
        SelectedIndex = 0;
    }

    /// <summary>
    /// Pages through the server's history on this device and appends what matches the query, as it is found. On a failure
    /// what was found stays, the row comes back to retry, and the user is told. Never throws.
    /// </summary>
    public async Task SearchOlderAsync()
    {
        var query = Query.Trim();
        if (_searchOlder is null || query.Length == 0 || IsSearchingOlder) return;
        Cancel();
        var search = new CancellationTokenSource();
        _search = search;
        _olderQuery = query;
        _olderDone = false;
        IsSearchingOlder = true;
        var row = Items.IndexOf(PopupItem.SearchOlder);
        if (row >= 0) Items.RemoveAt(row);
        try
        {
            await foreach (var clip in _searchOlder(query, search.Token).WithCancellation(search.Token))
            {
                if (search.IsCancellationRequested) return;
                Merge([clip]);
                if (SelectedIndex < 0 || SelectedIndex >= Items.Count) SelectedIndex = 0;
            }
            _olderDone = true;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is VaultKeyClosedException or VaultLockedException)
        {
            // Locked (a reset, a sign-out) meanwhile: not a network problem. What was found stays; the banner says the rest.
            _olderUnavailable = true;
        }
        catch (Exception)
        {
            if (search.IsCancellationRequested) return;
            _notify?.Invoke(UnreachableText);
        }
        finally
        {
            if (_search == search) IsSearchingOlder = false;
        }
        AddSearchOlderRow();
    }

    /// <remarks>
    /// Only the local cache (about 500 clips) is listed for a filter alone: the popup has no paging, and the server can't
    /// search, so "Search older clips" (which keeps the filter) is the only way to older ones.
    /// </remarks>
    private IReadOnlyList<CachedClip> CachedFor(string query) =>
        (Tab == PopupTab.History
            ? _cache.Search(query)
            : _cache.Pinned.Where(c => Matches(c.Clip, query)))
        .Where(c => PassesFilter(c.Clip)).ToArray();

    /// <summary>Whether a clip matches a search: its text, or any of its file names.</summary>
    public static bool Matches(ClipDto clip, string query) =>
        query.Length == 0
        || clip.Text is { } t && t.Contains(query, StringComparison.OrdinalIgnoreCase)
        || clip.Files is { } files && files.Any(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    private async Task ListServerPinsAsync(string query, CancellationToken ct)
    {
        try
        {
            if (_serverPinned is null) return;
            if (query.Length > 0) await Task.Delay(Debounce, _time, ct);
            var results = await _serverPinned(query, ct);
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
            if (PassesFilter(clip) && known.Add(clip.Id)) Items.Add(ToItem(clip, null, newest: false));
    }

    private PopupItem ToItem(ClipDto clip, byte[]? thumbnail, bool newest) =>
        new(clip, FormatPreview(clip), $"{clip.SourceDeviceName}, {Ago(clip.LastCopiedAt)}", thumbnail, newest);

    public static string FormatPreview(ClipDto clip)
    {
        if (clip.Kind == ClipKind.Image) return $"Image {clip.Width}×{clip.Height}";
        if (clip.Kind == ClipKind.Unknown) return PopupItem.UnknownText;
        if (clip.Kind == ClipKind.File) return FileNames.RowText(clip.Files ?? []);
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
        if (_setPinned is null || item.IsSearchOlder) return;
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

    /// <summary>
    /// Ctrl+S: saves the selected file clip's files to Downloads (downloading them if needed) and says where. Does nothing on
    /// any other row. Never throws, and never pastes.
    /// </summary>
    public async Task SaveSelectedAsync()
    {
        if (_save is null || Selected is not { IsFile: true } item) return;
        string? message;
        try { message = await _save(item.Clip); }
        catch (Exception) { message = CouldNotSaveText; }   // the error may name a file: never shown or logged
        if (message is not null) _notify?.Invoke(message);
    }

    /// <summary>Enter or a click: pastes the selected clip, or runs the older-clip search on that row. An unknown clip does nothing.</summary>
    public Task ActivateSelectedAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Items.Count) return Task.CompletedTask;
        var item = Items[SelectedIndex];
        if (item.IsSearchOlder) return SearchOlderAsync();
        return item.IsUnknown ? Task.CompletedTask : _activate(item.Clip);
    }

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
        var serverHits = query.Length == 0 && !pinnedTab ? []
            : Items.Where(i => !i.IsSearchOlder && !held.Contains(i.Clip.Id)).Select(i => i.Clip).ToList();
        var cached = CachedFor(query);

        Reset(cached, highlightNewest: !pinnedTab && query.Length == 0);
        foreach (var clip in serverHits) Items.Add(ToItem(clip, null, newest: false));
        AddSearchOlderRow();

        var index = selectedId is { } id ? Items.Select(i => i.Clip.Id).ToList().IndexOf(id) : -1;
        SelectedIndex = index >= 0 ? index : Math.Clamp(previousIndex, 0, Math.Max(0, Items.Count - 1));
    }

    /// <summary>Stops a pending server lookup or older-clip search (the query changed, or the window closed).</summary>
    public void Cancel()
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
    }
}
