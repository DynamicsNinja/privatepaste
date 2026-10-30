using System.Security.Claims;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Realtime;

namespace PrivatePaste.Server.Api;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").RequireAuthorization(AuthSchemes.SignedInPolicy).RequireAntiforgeryForCookieAuth();

        // The owner manages every device; a regular person only their own (someone else's is "not found").
        group.MapGet("", async (ClaimsPrincipal user, DeviceRepository devices) =>
            Results.Ok(await devices.ListAsync(user.DeviceId(), user.IsOwner() ? null : user.UserId())));

        group.MapDelete("{id:guid}", async (Guid id, ClaimsPrincipal user, DeviceRepository devices, ConnectionHub hub) =>
        {
            if (!await devices.RevokeAsync(id, user.IsOwner() ? null : user.UserId()))
                return Results.NotFound();
            await hub.CloseDeviceAsync(id);
            return Results.NoContent();
        });
    }
}
