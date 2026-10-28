using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PrivatePaste.Server.Auth;

namespace PrivatePaste.Server.Pages;

// The "login" policy only counts POSTs, so viewing the page is never limited.
[EnableRateLimiting("login")]
public class LoginModel(PasswordVerifier verifier) : PageModel
{
    [BindProperty]
    public string? Password { get; set; }

    public bool Failed { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        var check = await verifier.VerifyAsync(Password, HttpContext.RequestAborted);
        if (check == PasswordCheck.Busy)
            return StatusCode(StatusCodes.Status429TooManyRequests);
        if (check != PasswordCheck.Valid)
        {
            Failed = true;
            Password = null;
            return Page();
        }

        // No "sub" claim: the web session is not a device, so ToDeviceIdentity() yields (null, "Web", "web").
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "web-owner"),
                new Claim(DeviceClaims.Name, "Web"),
                new Claim(DeviceClaims.Platform, DeviceClaims.WebPlatform),
            ],
            AuthSchemes.Cookies);
        await HttpContext.SignInAsync(AuthSchemes.Cookies, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
        return Redirect("/");
    }
}
