using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Realtime;

namespace PrivatePaste.Server.Pages;

public class DevicesModel(DeviceRepository devices, ConnectionHub hub) : PageModel
{
    public IReadOnlyList<DeviceDto> Devices { get; private set; } = [];

    public async Task OnGetAsync() => Devices = await devices.ListAsync(null);

    public async Task<IActionResult> OnPostRevokeAsync(Guid id)
    {
        if (await devices.RevokeAsync(id))
            await hub.CloseDeviceAsync(id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Cookies);
        return Redirect("/login");
    }
}
