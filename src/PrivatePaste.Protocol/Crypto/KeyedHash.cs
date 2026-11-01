using System.Security.Cryptography;
using System.Text;

namespace PrivatePaste.Protocol.Crypto;

/// <summary>
/// The hash the server sees for dedup. A plain <see cref="ContentHash"/> would let anyone confirm a guessed clip,
/// so it is keyed with a vault subkey the server doesn't have.
/// </summary>
public static class KeyedHash
{
    /// <summary>Lowercase hex of HMAC-SHA256(hashKey, UTF8(<paramref name="contentHash"/>)).</summary>
    public static string For(VaultKey key, string contentHash) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key.HashKey, Encoding.UTF8.GetBytes(contentHash)));
}
