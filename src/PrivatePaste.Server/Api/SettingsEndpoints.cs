using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Api;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization(AuthSchemes.SignedInPolicy).RequireAntiforgeryForCookieAuth();

        group.MapGet("settings", async (SettingsRepository settings) =>
            Results.Ok(new SettingsDto(await settings.GetAutoDeleteDaysAsync())));

        group.MapPut("settings", async (SettingsDto body, SettingsRepository settings) =>
        {
            try
            {
                await settings.SetAutoDeleteDaysAsync(body.AutoDeleteDays);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    detail: "autoDeleteDays must be null, 7, 30 or 90.");
            }

            return Results.Ok(new SettingsDto(await settings.GetAutoDeleteDaysAsync()));
        }).RequireAuthorization(AuthSchemes.OwnerPolicy);
    }
}
