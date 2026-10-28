using Microsoft.AspNetCore.Antiforgery;
using PrivatePaste.Server.Auth;

namespace PrivatePaste.Server.Api;

public static class CookieAntiforgery
{
    public const string HeaderName = "RequestVerificationToken";

    /// <summary>
    /// Requires a valid antiforgery token (<see cref="HeaderName"/> header) on unsafe-method calls that are authenticated
    /// by the web-session cookie. Bearer-authenticated calls are not forgeable cross-site and are exempt.
    /// A missing header is rejected up front: otherwise antiforgery falls back to reading the token from the form,
    /// which would buffer a multipart upload (e.g. an image) to a plaintext temp file.
    /// </summary>
    public static TBuilder RequireAntiforgeryForCookieAuth<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
                && !HttpMethods.IsOptions(http.Request.Method)
                && http.User.Identity?.AuthenticationType == AuthSchemes.Cookies
                && (!http.Request.Headers.ContainsKey(HeaderName)
                    || !await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http)))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Invalid antiforgery token.");
            }

            return await next(ctx);
        });
}
