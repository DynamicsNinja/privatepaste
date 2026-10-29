using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Pages;

public class IndexModel(ClipRepository clips, ClipService service, TimeProvider time) : PageModel
{
    private const int PageSize = 50;
    private const int PreviewLength = 200;

    public IReadOnlyList<ClipDto> Items { get; private set; } = [];
    public string? NextCursor { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>"pinned" shows the Pinned tab; anything else is History.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public bool PinnedTab => string.Equals(Tab, "pinned", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tab query value to carry through links and redirects (null for History, the default).</summary>
    public string? TabRoute => PinnedTab ? "pinned" : null;

    public async Task<IActionResult> OnGetAsync(string? cursor)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(Q) ? null : Q.Trim();
            var page = PinnedTab
                ? await clips.ListPinnedAsync(query, cursor, PageSize)
                : await clips.ListAsync(query, cursor, PageSize);
            Items = page.Items;
            NextCursor = page.NextCursor;
        }
        catch (ArgumentException)
        {
            return BadRequest("Invalid cursor.");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        await service.DeleteAsync(id);
        return Back();
    }

    public async Task<IActionResult> OnPostPinAsync(Guid id)
    {
        await service.SetPinnedAsync(id, true);
        return Back();
    }

    public async Task<IActionResult> OnPostUnpinAsync(Guid id)
    {
        await service.SetPinnedAsync(id, false);
        return Back();
    }

    private IActionResult Back() =>
        RedirectToPage(new { q = string.IsNullOrWhiteSpace(Q) ? null : Q, tab = TabRoute });

    public static string Preview(string? text) =>
        text is null ? "" : text.Length <= PreviewLength ? text : text[..PreviewLength] + "...";

    /// <summary>Only http(s) URLs become anchors; the classifier already guarantees this for link clips.</summary>
    public static bool IsSafeUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public string Ago(DateTimeOffset at)
    {
        var span = time.GetUtcNow() - at;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} d ago";
        return at.ToString("yyyy-MM-dd");
    }
}
