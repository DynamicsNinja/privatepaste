using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Realtime;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Auth;

/// <summary>The outcome of a change to people; <see cref="Error"/> is the copy to show when it was refused.</summary>
public sealed record PeopleResult(bool Ok, string? Error)
{
    public static readonly PeopleResult Success = new(true, null);

    public static PeopleResult Fail(string error) => new(false, error);
}

/// <summary>
/// Adds, renames, resets and removes people, and changes a person's own password. Clips only carry names while there
/// is more than one current person, so a change that crosses that line, or a rename, re-sends the affected clips.
/// </summary>
public sealed class PeopleService(
    UserRepository users,
    PasswordVerifier verifier,
    DeviceRepository devices,
    ConnectionHub hub,
    ClipService clips,
    SettingsRepository settings)
{
    public const int MinPasswordLength = 8;

    public const string BusyError = "Too many attempts. Wait a minute and try again.";
    public const string NameTakenError = "That name is already taken.";
    public const string ShortPasswordError = "Password must be at least 8 characters.";
    public const string LongPasswordError = "Password must be at most 1024 characters.";
    public const string WrongCurrentPasswordError = "Current password is wrong.";
    public const string MismatchError = "Passwords don't match.";
    public const string ResetOwnerError = "Use Change password in Settings.";
    public const string RemoveOwnerError = "The owner can't be removed.";
    public const string UnknownPersonError = "That person no longer exists.";

    // Serializes changes that count people, so two of them can't both miss the 1 <-> 2 crossing.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<PeopleResult> AddAsync(string? name, string? password)
    {
        if (UserRepository.ValidateName(name) is { } nameError)
            return PeopleResult.Fail(nameError);
        if (ValidatePassword(password) is { } passwordError)
            return PeopleResult.Fail(passwordError);
        // Hashed outside the lock: it takes a while and needs nothing the lock guards.
        if (await verifier.HashAsync(password!) is not { } hash)
            return PeopleResult.Fail(BusyError);

        await _lock.WaitAsync();
        try
        {
            try
            {
                await users.CreateAsync(name!, hash, isOwner: false);
            }
            catch (NameTakenException)
            {
                return PeopleResult.Fail(NameTakenError);
            }
            if (await users.CountCurrentAsync() == 2)
                await clips.ResendAsync(null);
            return PeopleResult.Success;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PeopleResult> RenameAsync(Guid id, string? name)
    {
        if (UserRepository.ValidateName(name) is { } nameError)
            return PeopleResult.Fail(nameError);

        await _lock.WaitAsync();
        try
        {
            if (await CurrentAsync(id) is not { } user)
                return PeopleResult.Fail(UnknownPersonError);
            var newName = name!.Trim();
            if (newName == user.Name)
                return PeopleResult.Success;
            try
            {
                await users.RenameAsync(id, newName);
            }
            catch (NameTakenException)
            {
                return PeopleResult.Fail(NameTakenError);
            }
            if (await users.CountCurrentAsync() > 1)
                await clips.ResendAsync(id);
            return PeopleResult.Success;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sets a new password for someone else and signs out everything they are signed in on.</summary>
    public async Task<PeopleResult> ResetPasswordAsync(Guid id, string? newPassword)
    {
        if (await CurrentAsync(id) is not { } user)
            return PeopleResult.Fail(UnknownPersonError);
        if (user.IsOwner)
            return PeopleResult.Fail(ResetOwnerError);
        if (ValidatePassword(newPassword) is { } passwordError)
            return PeopleResult.Fail(passwordError);
        if (await verifier.HashAsync(newPassword!) is not { } hash)
            return PeopleResult.Fail(BusyError);

        await users.SetPasswordAsync(id, hash, newSecurityStamp: true);
        await SignOutDevicesAsync(id);
        return PeopleResult.Success;
    }

    public async Task<PeopleResult> RemoveAsync(Guid id)
    {
        await _lock.WaitAsync();
        try
        {
            if (await CurrentAsync(id) is not { } user)
                return PeopleResult.Fail(UnknownPersonError);
            if (user.IsOwner)
                return PeopleResult.Fail(RemoveOwnerError);

            var before = await users.CountCurrentAsync();
            await users.MarkRemovedAsync(id);
            await SignOutDevicesAsync(id);
            if (before == 2 && await users.CountCurrentAsync() == 1)
                await clips.ResendAsync(null);
            return PeopleResult.Success;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A person changing their own password: their devices and web sessions stay signed in.</summary>
    public async Task<PeopleResult> ChangeOwnPasswordAsync(Guid id, string? current, string? newPassword, string? repeat)
    {
        if (await CurrentAsync(id) is not { } user)
            return PeopleResult.Fail(UnknownPersonError);
        if (ValidatePassword(newPassword) is { } passwordError)
            return PeopleResult.Fail(passwordError);
        if (newPassword != repeat)
            return PeopleResult.Fail(MismatchError);

        switch (await verifier.VerifyAsync(current, user.PasswordHash))
        {
            case PasswordCheck.Busy:
                return PeopleResult.Fail(BusyError);
            case PasswordCheck.Invalid:
                return PeopleResult.Fail(WrongCurrentPasswordError);
        }
        if (await verifier.HashAsync(newPassword!) is not { } hash)
            return PeopleResult.Fail(BusyError);

        await users.SetPasswordAsync(id, hash, newSecurityStamp: false);
        // Cookies from before there were people may be held by anyone the old shared password was given to; the
        // owner changing their password is how those end.
        if (user.IsOwner)
            await settings.EndLegacyWebSessionsAsync();
        return PeopleResult.Success;
    }

    private static string? ValidatePassword(string? password) => password switch
    {
        null or { Length: < MinPasswordLength } => ShortPasswordError,
        { Length: > PasswordVerifier.MaxPasswordLength } => LongPasswordError,
        _ => null,
    };

    private async Task<UserRecord?> CurrentAsync(Guid id) =>
        await users.GetAsync(id) is { RemovedAt: null } user ? user : null;

    private async Task SignOutDevicesAsync(Guid id)
    {
        foreach (var deviceId in await devices.RevokeAllForUserAsync(id))
            await hub.CloseDeviceAsync(deviceId);
    }
}
