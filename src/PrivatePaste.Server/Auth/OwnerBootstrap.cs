using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Auth;

/// <summary>
/// Makes sure the server has an owner. On the first start (or the upgrade from one shared password) the owner is
/// created with PP_PASSWORD and given every existing device and clip. Afterwards PP_PASSWORD is the recovery path:
/// changing it resets the owner's password, while restarting with the same value keeps one set on the web.
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

    public async Task EnsureOwnerAsync()
    {
        var owner = await users.GetOwnerAsync()
                    ?? await users.CreateAsync(OwnerName, await HashAsync(options.Password), isOwner: true);

        var fingerprint = await settings.GetAsync(FingerprintSetting);
        if (fingerprint is null)
        {
            // The owner was just created, or a first start stopped before the fingerprint was stored.
            await GiveUnownedRowsToAsync(owner.Id);
        }
        else if (await verifier.VerifyAsync(options.Password, fingerprint) == PasswordCheck.Valid)
        {
            return;
        }
        else
        {
            // Not a new stamp: the owner's own devices and sessions stay signed in.
            await users.SetPasswordAsync(owner.Id, await HashAsync(options.Password), newSecurityStamp: false);
            logger.LogWarning("The owner's password was reset from PP_PASSWORD.");
        }

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

    // Nothing else hashes during startup, so a full gate only means a moment's wait.
    private async Task<string> HashAsync(string password)
    {
        while (true)
        {
            if (await verifier.HashAsync(password) is { } hash)
                return hash;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }
}
