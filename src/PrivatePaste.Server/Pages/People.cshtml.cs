using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Realtime;

namespace PrivatePaste.Server.Pages;

/// <summary>A person's card: who they are and the app devices they are signed in on.</summary>
public sealed record PersonCard(UserRecord Person, IReadOnlyList<DeviceDto> Devices);

/// <summary>
/// The owner sees everyone, adds people and renames, resets or removes them; anyone else sees only their own card.
/// Owner-only handlers answer 403 to anyone else, so hiding the buttons is not what protects them.
/// </summary>
public class PeopleModel(UserRepository users, DeviceRepository devices, ConnectionHub hub, PeopleService people,
    TimeProvider time) : PageModel
{
    public IReadOnlyList<PersonCard> Cards { get; private set; } = [];
    public bool IsOwner => User.IsOwner();

    /// <summary>The person whose rename form is open (from <c>?rename=</c>, or a failed rename).</summary>
    [BindProperty(SupportsGet = true, Name = "rename")]
    public Guid? RenameId { get; set; }

    /// <summary>The person whose reset-password form is open (from <c>?reset=</c>, or a failed reset).</summary>
    [BindProperty(SupportsGet = true, Name = "reset")]
    public Guid? ResetId { get; set; }

    /// <summary>What the add form or the open rename form shows after a refused change.</summary>
    public string? TypedName { get; private set; }

    public string? AddError { get; private set; }

    /// <summary>A refused rename, reset or removal, shown on that person's card.</summary>
    public (Guid PersonId, string Message)? CardError { get; private set; }

    public string Seen(DeviceDto device) =>
        device.LastSeenAt is { } seen ? "Seen " + TimeAgo.Format(seen, time.GetUtcNow()) : "Not seen yet";

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(string? name, string? password)
    {
        if (!IsOwner)
            return Forbidden();
        var result = await people.AddAsync(name, password);
        if (result.Ok)
            return RedirectToPage();
        AddError = result.Error;
        TypedName = name;
        return await ShowAsync();
    }

    public async Task<IActionResult> OnPostRenameAsync(Guid id, string? name)
    {
        if (!IsOwner)
            return Forbidden();
        var result = await people.RenameAsync(id, name);
        if (result.Ok)
            return RedirectToPage();
        CardError = (id, result.Error!);
        RenameId = id;
        TypedName = name;
        return await ShowAsync();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(Guid id, string? password)
    {
        if (!IsOwner)
            return Forbidden();
        var result = await people.ResetPasswordAsync(id, password);
        if (result.Ok)
            return RedirectToPage();
        CardError = (id, result.Error!);
        ResetId = id;
        return await ShowAsync();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id)
    {
        if (!IsOwner)
            return Forbidden();
        var result = await people.RemoveAsync(id);
        if (result.Ok)
            return RedirectToPage();
        CardError = (id, result.Error!);
        return await ShowAsync();
    }

    /// <summary>The owner can sign out any device; anyone else only their own.</summary>
    public async Task<IActionResult> OnPostSignOutDeviceAsync(Guid id)
    {
        if (await devices.RevokeAsync(id, IsOwner ? null : User.UserId()))
            await hub.CloseDeviceAsync(id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Cookies);
        return Redirect("/login");
    }

    private async Task<IActionResult> ShowAsync()
    {
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<UserRecord> shown;
        if (IsOwner)
            shown = await users.ListCurrentAsync();
        else
            shown = User.UserId() is { } self && await users.GetAsync(self) is { RemovedAt: null } me ? [me] : [];

        var cards = new List<PersonCard>(shown.Count);
        foreach (var person in shown)
            cards.Add(new PersonCard(person, await devices.ListAsync(null, person.Id)));
        Cards = cards;
    }

    // Forbid() would send the cookie scheme's access-denied redirect; a plain 403 is what a refused post deserves.
    private StatusCodeResult Forbidden() => StatusCode(StatusCodes.Status403Forbidden);
}
