using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace PrivatePaste.Server.Auth;

public enum PasswordCheck
{
    Valid,
    Invalid,

    /// <summary>Too many hashes are already running or waiting; the caller should answer 429.</summary>
    Busy,
}

/// <summary>
/// Hashes passwords with Argon2id, each with its own random salt, and checks candidates against a stored hash in fixed
/// time. Each hash needs 64 MiB, so at most <see cref="MaxConcurrentHashes"/> run at once (with a short queue) to bound
/// memory use under attack.
/// </summary>
public sealed class PasswordVerifier
{
    public const int MaxConcurrentHashes = 2;
    public const int MaxQueuedHashes = 4;
    public const int MaxPasswordLength = 1024;

    private const string Prefix = "argon2id";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // Hashed against when there is no stored hash (an unknown name), so that costs the same as a wrong password.
    private static readonly byte[] DummySalt = new byte[SaltBytes];

    private readonly ConcurrencyGate _gate = new(MaxConcurrentHashes, MaxQueuedHashes);

    /// <summary>Returns <c>argon2id$&lt;salt&gt;$&lt;hash&gt;</c> (base64), or null when the gate is full.</summary>
    public async Task<string?> HashAsync(string password, CancellationToken ct = default)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        using var lease = await _gate.TryEnterAsync(ct);
        if (lease is null)
            return null;
        return $"{Prefix}${Convert.ToBase64String(salt)}${Convert.ToBase64String(Hash(password, salt))}";
    }

    /// <summary>
    /// Checks <paramref name="candidate"/> against <paramref name="stored"/>. A null or unreadable stored hash still
    /// costs one hash and is <see cref="PasswordCheck.Invalid"/>.
    /// </summary>
    public async Task<PasswordCheck> VerifyAsync(string? candidate, string? stored, CancellationToken ct = default)
    {
        if (candidate is null || candidate.Length > MaxPasswordLength)
            return PasswordCheck.Invalid;
        using var lease = await _gate.TryEnterAsync(ct);
        if (lease is null)
            return PasswordCheck.Busy;

        if (!TryParse(stored, out var salt, out var expected))
        {
            Hash(candidate, DummySalt);
            return PasswordCheck.Invalid;
        }
        return CryptographicOperations.FixedTimeEquals(Hash(candidate, salt), expected)
            ? PasswordCheck.Valid
            : PasswordCheck.Invalid;
    }

    private static bool TryParse(string? stored, out byte[] salt, out byte[] hash)
    {
        salt = hash = [];
        var parts = stored?.Split('$');
        if (parts is not [Prefix, var s, var h])
            return false;
        try
        {
            salt = Convert.FromBase64String(s);
            hash = Convert.FromBase64String(h);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length == SaltBytes && hash.Length == HashBytes;
    }

    private static byte[] Hash(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = 64 * 1024, // KiB
            Iterations = 3,
            DegreeOfParallelism = 1,
        };
        return argon.GetBytes(HashBytes);
    }
}
