using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PrivatePaste.Server.Pages;

/// <summary>
/// The history page is a shell: the browser fetches the clips, decrypts them with the vault key and builds the rows
/// (wwwroot/js/history.js). The server only holds ciphertext, so it renders no clip data and has no row handlers;
/// Pin and Delete go through the API.
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>The search box's starting text; the browser does the searching.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>"pinned" shows the Pinned tab; anything else is History.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public bool PinnedTab => string.Equals(Tab, "pinned", StringComparison.OrdinalIgnoreCase);

    public void OnGet()
    {
    }

    // Razor Pages renders the page for a POST it has no handler for; a POST here (an old page's Pin or Delete form)
    // gets a 404 instead of a page that looks as if it worked.
    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        if (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method))
            context.Result = NotFound();
    }
}
