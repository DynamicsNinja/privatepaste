namespace PrivatePaste.Server.Auth;

/// <summary>Checks a name and password. An unknown name costs one hash, like a wrong password, and looks the same.</summary>
public sealed class SignInVerifier(UserRepository users, PasswordVerifier verifier)
{
    /// <returns>The person when <see cref="PasswordCheck.Valid"/>, otherwise null.</returns>
    public async Task<(PasswordCheck Check, UserRecord? User)> VerifyAsync(string? name, string? password,
        CancellationToken ct)
    {
        // A name that could never be valid can't belong to anyone; skip the lookup but not the hash.
        var user = UserRepository.ValidateName(name) is null ? await users.FindCurrentByNameAsync(name!) : null;
        var check = await verifier.VerifyAsync(password, user?.PasswordHash, ct);
        return check == PasswordCheck.Valid ? (check, user) : (check, null);
    }
}
