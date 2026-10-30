using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PrivatePaste.Server.Auth;

namespace PrivatePaste.Server.Pages;

// The "login" policy only counts POSTs, so viewing the page is never limited.
[EnableRateLimiting("login")]
public class LoginModel(SignInVerifier signIn) : PageModel
{
    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public bool Failed { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        var (check, user) = await signIn.VerifyAsync(Name, Password, HttpContext.RequestAborted);
        if (check == PasswordCheck.Busy)
            return StatusCode(StatusCodes.Status429TooManyRequests);
        if (user is null)
        {
            Failed = true;
            Password = null;
            return Page();
        }

        await HttpContext.SignInAsync(AuthSchemes.Cookies, WebSessionValidator.CreatePrincipal(user),
            new AuthenticationProperties { IsPersistent = true });
        return Redirect("/");
    }
}
