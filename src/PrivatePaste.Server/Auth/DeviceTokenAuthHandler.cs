using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PrivatePaste.Server.Clips;

namespace PrivatePaste.Server.Auth;

public static class AuthSchemes
{
    public const string DeviceToken = "DeviceToken";
    public const string Cookies = "Cookies";
    public const string OwnerPolicy = "Owner";
}

public static class DeviceClaims
{
    public const string Subject = "sub";
    public const string Name = "name";

    /// <summary>The calling device, or null when the caller is not a device-token principal.</summary>
    public static Guid? DeviceId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Subject), out var id) ? id : null;

    public static DeviceIdentity ToDeviceIdentity(this ClaimsPrincipal user) =>
        new(user.DeviceId(), user.FindFirstValue(Name) ?? "Web");
}

public sealed class DeviceTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DeviceRepository devices) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string Prefix = "Bearer ";
    private const int MaxTokenLength = 128;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header[Prefix.Length..].Trim();
        if (token.Length == 0)
            return AuthenticateResult.NoResult();
        if (token.Length > MaxTokenLength)
            return AuthenticateResult.Fail("Invalid token.");

        var device = await devices.FindByTokenAsync(token);
        if (device is null)
            return AuthenticateResult.Fail("Invalid token.");

        var identity = new ClaimsIdentity(
            [new Claim(DeviceClaims.Subject, device.Id.ToString()), new Claim(DeviceClaims.Name, device.Name)],
            Scheme.Name, DeviceClaims.Name, roleType: null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
