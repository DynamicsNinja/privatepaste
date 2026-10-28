using System.Security.Claims;
using PrivatePaste.Server.Auth;

namespace PrivatePaste.Server.Api;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").RequireAuthorization(AuthSchemes.OwnerPolicy);

        group.MapGet("", async (ClaimsPrincipal user, DeviceRepository devices) =>
            Results.Ok(await devices.ListAsync(user.DeviceId())));

        group.MapDelete("{id:guid}", async (Guid id, DeviceRepository devices) =>
            await devices.RevokeAsync(id) ? Results.NoContent() : Results.NotFound());
    }
}
