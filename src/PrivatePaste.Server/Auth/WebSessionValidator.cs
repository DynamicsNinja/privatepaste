using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Auth;

/// <summary>Keeps web sessions tied to a current person: checked on every cookie-authenticated request.</summary>
public static class WebSessionValidator
{
    // What every web session carried before there were people; only the owner could sign in then. Honoured only
    // while SettingsRepository allows legacy web sessions.
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
        var services = ctx.HttpContext.RequestServices;
        var users = services.GetRequiredService<UserRepository>();

        // A legacy session keeps its marker after the upgrade below, so turning legacy sessions off still ends it.
        var legacy = principal.FindFirstValue(ClaimTypes.NameIdentifier) == LegacyOwnerId;
        if (legacy && !await services.GetRequiredService<SettingsRepository>().GetLegacyWebSessionsAllowedAsync())
        {
            await RejectAsync(ctx);
            return;
        }

        if (principal.UserId() is not { } userId)
        {
            if (legacy && await users.GetOwnerAsync() is { } owner)
            {
                ctx.ReplacePrincipal(CreateLegacyOwnerPrincipal(owner));
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

    private static ClaimsPrincipal CreateLegacyOwnerPrincipal(UserRecord owner)
    {
        var principal = CreatePrincipal(owner);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.NameIdentifier, LegacyOwnerId));
        return principal;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(AuthSchemes.Cookies);
    }
}
