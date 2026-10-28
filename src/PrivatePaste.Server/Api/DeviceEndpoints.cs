using System.Security.Claims;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Realtime;

namespace PrivatePaste.Server.Api;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").RequireAuthorization(AuthSchemes.OwnerPolicy).RequireAntiforgeryForCookieAuth();

        group.MapGet("", async (ClaimsPrincipal user, DeviceRepository devices) =>
            Results.Ok(await devices.ListAsync(user.DeviceId())));

        group.MapDelete("{id:guid}", async (Guid id, DeviceRepository devices, ConnectionHub hub) =>
        {
            if (!await devices.RevokeAsync(id))
                return Results.NotFound();
            await hub.CloseDeviceAsync(id);
            return Results.NoContent();
        });
    }
}
