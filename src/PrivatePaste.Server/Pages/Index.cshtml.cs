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

    public async Task<IActionResult> OnGetAsync(string? cursor)
    {
        try
        {
            var page = await clips.ListAsync(string.IsNullOrWhiteSpace(Q) ? null : Q.Trim(), cursor, PageSize);
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
        return RedirectToPage(new { q = string.IsNullOrWhiteSpace(Q) ? null : Q });
    }

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
