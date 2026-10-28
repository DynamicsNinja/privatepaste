using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using PrivatePaste.Server.Config;

namespace PrivatePaste.Server.Auth;

/// <summary>Hashes the configured password with Argon2id once and compares candidates in fixed time.</summary>
public sealed class PasswordVerifier
{
    private const int MaxCandidateLength = 1024;

    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _hash;

    public PasswordVerifier(ServerOptions options) => _hash = Hash(options.Password);

    public bool Verify(string? candidate)
    {
        if (candidate is null || candidate.Length > MaxCandidateLength)
            return false;
        return CryptographicOperations.FixedTimeEquals(Hash(candidate), _hash);
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
