using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace PrivatePaste.Server.Auth;

/// <summary>Keeps web sessions tied to a current person: checked on every cookie-authenticated request.</summary>
public static class WebSessionValidator
{
    // What every web session carried before there were people; only the owner could sign in then.
    private const string LegacyOwnerId = "web-owner";

    /// <summary>The cookie principal for <paramref name="user"/>.</summary>
    public static ClaimsPrincipal CreatePrincipal(UserRecord user)
    {
        // No "sub" claim: the web session is not a device, so ToDeviceIdentity() yields (null, "Web", "web").
        var identity = new ClaimsIdentity(
            [
                new Claim(DeviceClaims.Name, "Web"),
                new Claim(DeviceClaims.Platform, DeviceClaims.WebPlatform),
                ..UserClaims.For(user.Id, user.IsOwner),
                new Claim(UserClaims.Stamp, user.SecurityStamp),
            ],
            AuthSchemes.Cookies);
        return new ClaimsPrincipal(identity);
    }

    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        if (principal is null)
            return;
        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserRepository>();

        if (principal.UserId() is not { } userId)
        {
            if (principal.FindFirstValue(ClaimTypes.NameIdentifier) == LegacyOwnerId
                && await users.GetOwnerAsync() is { } owner)
            {
                ctx.ReplacePrincipal(CreatePrincipal(owner));
                ctx.ShouldRenew = true;
            }
            else
            {
                await RejectAsync(ctx);
            }
            return;
        }

        var user = await users.GetAsync(userId);
        if (user is null || user.RemovedAt is not null || principal.FindFirstValue(UserClaims.Stamp) != user.SecurityStamp)
            await RejectAsync(ctx);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(AuthSchemes.Cookies);
    }
}
