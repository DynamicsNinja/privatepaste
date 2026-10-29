using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Pages;

public class SettingsModel(SettingsRepository settings, ClipRepository clips, ClipService service) : PageModel
{
    public int? AutoDeleteDays { get; private set; }
    public long UnpinnedCount { get; private set; }

    public string ConfirmClear =>
        $"Delete {UnpinnedCount} {(UnpinnedCount == 1 ? "clip" : "clips")} from every device? Pinned clips stay.";

    public async Task OnGetAsync()
    {
        AutoDeleteDays = await settings.GetAutoDeleteDaysAsync();
        UnpinnedCount = await clips.CountUnpinnedAsync();
    }

    /// <summary>No-JS fallback; the page normally saves through PUT /api/settings on change.</summary>
    public async Task<IActionResult> OnPostAutoDeleteAsync(int? days)
    {
        try
        {
            await settings.SetAutoDeleteDaysAsync(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest("Auto-delete is off or 7, 30 or 90 days.");
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        await service.ClearAsync();
        return RedirectToPage();
    }
}
