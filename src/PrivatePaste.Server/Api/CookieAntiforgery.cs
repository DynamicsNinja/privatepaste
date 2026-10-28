using Microsoft.AspNetCore.Antiforgery;
using PrivatePaste.Server.Auth;

namespace PrivatePaste.Server.Api;

public static class CookieAntiforgery
{
    public const string HeaderName = "RequestVerificationToken";

    /// <summary>
    /// Requires a valid antiforgery token (<see cref="HeaderName"/> header) on unsafe-method calls that are authenticated
    /// by the web-session cookie. Bearer-authenticated calls are not forgeable cross-site and are exempt.
    /// </summary>
    public static TBuilder RequireAntiforgeryForCookieAuth<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
                && !HttpMethods.IsOptions(http.Request.Method)
                && http.User.Identity?.AuthenticationType == AuthSchemes.Cookies
                && !await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid antiforgery token.");
            }

            return await next(ctx);
        });
}
