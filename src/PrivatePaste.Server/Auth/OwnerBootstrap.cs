using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Auth;

/// <summary>
/// Makes sure the server has an owner. On the first start (or the upgrade from one shared password) the owner is
/// created with PP_PASSWORD and given every existing device and clip. Afterwards PP_PASSWORD is the recovery path:
/// changing it resets the owner's password and ends their web sessions, while restarting with the same value keeps one
/// set on the web.
/// </summary>
public sealed class OwnerBootstrap(
    UserRepository users,
    PasswordVerifier verifier,
    SettingsRepository settings,
    PrivatePasteDb db,
    ServerOptions options,
    ILogger<OwnerBootstrap> logger)
{
    public const string OwnerName = "owner";

    // A hash of the PP_PASSWORD the owner's password last came from, to tell a changed PP_PASSWORD from a restart.
    private const string FingerprintSetting = "pp_password_hash";

    private static readonly TimeSpan GateRetryDelay = TimeSpan.FromMilliseconds(100);

    public async Task EnsureOwnerAsync()
    {
        var owner = await users.GetOwnerAsync();
        if (owner is null)
        {
            owner = await users.CreateAsync(OwnerName, await HashAsync(options.Password), isOwner: true);
            // Browsers signed in with the old shared password stay signed in, as the owner, until the owner's
            // password changes.
            await settings.AllowLegacyWebSessionsAsync();
        }

        // On every start, not just the first: after a rollback to a release without people and a redeploy, devices
        // and clips the old release created have no owner, and such a device's token would stop working.
        await GiveUnownedRowsToAsync(owner.Id);

        var fingerprint = await settings.GetAsync(FingerprintSetting);
        if (fingerprint is not null)
        {
            if (await VerifyAsync(options.Password, fingerprint) == PasswordCheck.Valid)
                return;
            // A reset means the old password may have been known to someone else, so every web session as the owner
            // ends, the ones from the old shared password included. Device tokens don't depend on the stamp: the
            // owner's devices stay signed in.
            await users.SetPasswordAsync(owner.Id, await HashAsync(options.Password), newSecurityStamp: true);
            await settings.EndLegacyWebSessionsAsync();
            logger.LogWarning("The owner's password was reset from PP_PASSWORD.");
        }
        // Otherwise the owner was just created, or a first start stopped before the fingerprint was stored.

        await settings.SetAsync(FingerprintSetting, await HashAsync(options.Password));
    }

    private async Task GiveUnownedRowsToAsync(Guid ownerId)
    {
        await using var conn = db.Open();
        await using var tx = conn.BeginTransaction();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE devices SET user_id = @owner WHERE user_id IS NULL;
            UPDATE clips SET source_user_id = @owner WHERE source_user_id IS NULL;
            """;
        cmd.Parameters.AddWithValue("@owner", ownerId.ToString());
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    // Nothing else hashes during startup, so a full gate only means a moment's wait. Busy must never pass for a
    // changed PP_PASSWORD: that would reset the owner's password.
    private async Task<string> HashAsync(string password)
    {
        while (true)
        {
            if (await verifier.HashAsync(password) is { } hash)
                return hash;
            await Task.Delay(GateRetryDelay);
        }
    }

    private async Task<PasswordCheck> VerifyAsync(string password, string stored)
    {
        while (true)
        {
            var check = await verifier.VerifyAsync(password, stored);
            if (check != PasswordCheck.Busy)
                return check;
            await Task.Delay(GateRetryDelay);
        }
    }
}
