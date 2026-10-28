using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop.ViewModels;

/// <summary>One popup row. <see cref="IsNewest"/> is true only for the first row while the query is empty.</summary>
public sealed record PopupItem(ClipDto Clip, string Preview, string Meta, byte[]? Thumbnail, bool IsNewest = false)
{
    public bool IsImage => Clip.Kind == ClipKind.Image;
    public bool IsLink => Clip.Kind == ClipKind.Link;
    public bool IsText => Clip.Kind == ClipKind.Text;
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
    private CancellationTokenSource? _search;

    public PopupViewModel(
        HistoryCache cache,
        Func<string, CancellationToken, Task<IReadOnlyList<ClipDto>>> serverSearch,
        Func<ClipDto, Task> activate,
        TimeProvider time)
    {
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

    partial void OnQueryChanged(string value)
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = null;

        var query = value.Trim();
        Reset(_cache.Search(query), highlightNewest: query.Length == 0);
        if (query.Length == 0) return;

        _search = new CancellationTokenSource();
        _ = SearchServerAsync(query, _search.Token);
    }

    private async Task SearchServerAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, _time, ct);
            var results = await _serverSearch(query, ct);
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

    public Task ActivateSelectedAsync() =>
        SelectedIndex >= 0 && SelectedIndex < Items.Count ? _activate(Items[SelectedIndex].Clip) : Task.CompletedTask;

    /// <summary>Stops a pending server search (the window closed).</summary>
    public void Cancel()
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
    }
}
