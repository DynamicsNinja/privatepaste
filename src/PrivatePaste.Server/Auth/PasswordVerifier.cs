using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using PrivatePaste.Server.Config;

namespace PrivatePaste.Server.Auth;

public enum PasswordCheck
{
    Valid,
    Invalid,

    /// <summary>Too many verifications are already running or waiting; the caller should answer 429.</summary>
    Busy,
}

/// <summary>
/// Hashes the configured password with Argon2id once and compares candidates in fixed time. Each hash needs 64 MiB,
/// so at most <see cref="MaxConcurrentHashes"/> run at once (with a short queue) to bound memory use under attack.
/// </summary>
public sealed class PasswordVerifier
{
    public const int MaxConcurrentHashes = 2;
    public const int MaxQueuedHashes = 4;
    private const int MaxCandidateLength = 1024;

    private readonly ConcurrencyGate _gate = new(MaxConcurrentHashes, MaxQueuedHashes);

    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _hash;

    public PasswordVerifier(ServerOptions options) => _hash = Hash(options.Password);

    public async Task<PasswordCheck> VerifyAsync(string? candidate, CancellationToken ct = default)
    {
        if (candidate is null || candidate.Length > MaxCandidateLength)
            return PasswordCheck.Invalid;
        using var lease = await _gate.TryEnterAsync(ct);
        if (lease is null)
            return PasswordCheck.Busy;
        return CryptographicOperations.FixedTimeEquals(Hash(candidate), _hash) ? PasswordCheck.Valid : PasswordCheck.Invalid;
    }

    private byte[] Hash(string password)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = _salt,
            MemorySize = 64 * 1024, // KiB
            Iterations = 3,
            DegreeOfParallelism = 1,
        };
        return argon.GetBytes(32);
    }
}
