using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Pages;

/// <summary>Everyone changes their own password here; auto-delete and Clear history are the owner's.</summary>
// A password change costs two Argon2 hashes, so it counts against the sign-in limit; the owner's other posts don't.
[EnableRateLimiting("login")]
[LoginAttemptHandlers("ChangePassword")]
public class SettingsModel(SettingsRepository settings, ClipRepository clips, ClipService service, PeopleService people)
    : PageModel
{
    public const string PasswordChangedMessage = "Password changed.";

    public bool IsOwner => User.IsOwner();
    public int? AutoDeleteDays { get; private set; }
    public long UnpinnedCount { get; private set; }

    public string? PasswordError { get; private set; }

    [TempData]
    public string? PasswordStatus { get; set; }

    public string ConfirmClear =>
        $"Delete {UnpinnedCount} {(UnpinnedCount == 1 ? "clip" : "clips")} from every device? Pinned clips stay.";

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostChangePasswordAsync(string? current, string? password, string? repeat)
    {
        if (User.UserId() is not { } self)
            return Forbidden();
        var result = await people.ChangeOwnPasswordAsync(self, current, password, repeat);
        if (result is { Ok: true, User: { } changed })
        {
            // The change gave the person a new stamp, which ends their other web sessions; this one carries on.
            await HttpContext.SignInAsync(AuthSchemes.Cookies, WebSessionValidator.CreatePrincipal(changed),
                new AuthenticationProperties { IsPersistent = true });
            PasswordStatus = PasswordChangedMessage;
            return RedirectToPage();
        }
        PasswordError = result.Error;
        await LoadAsync();
        return Page();
    }

    /// <summary>No-JS fallback; the page normally saves through PUT /api/settings on change.</summary>
    public async Task<IActionResult> OnPostAutoDeleteAsync(int? days)
    {
        if (!IsOwner)
            return Forbidden();
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
        if (!IsOwner)
            return Forbidden();
        await service.ClearAsync();
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        if (!IsOwner)
            return;
        AutoDeleteDays = await settings.GetAutoDeleteDaysAsync();
        UnpinnedCount = await clips.CountUnpinnedAsync();
    }

    // Forbid() would send the cookie scheme's access-denied redirect; a plain 403 is what a refused post deserves.
    private StatusCodeResult Forbidden() => StatusCode(StatusCodes.Status403Forbidden);
}
