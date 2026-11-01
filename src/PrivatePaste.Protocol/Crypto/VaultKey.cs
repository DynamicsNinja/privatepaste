using System.Security.Cryptography;
using System.Text;

namespace PrivatePaste.Protocol.Crypto;

/// <summary>
/// The per-server vault key (VK) and the subkeys every client derives from it. The server never sees any of these.
/// </summary>
public sealed class VaultKey : IDisposable
{
    public const int Length = 32;

    private readonly byte[] _vk;
    private readonly byte[] _encKey;
    private readonly byte[] _hashKey;
    private bool _disposed;

    private VaultKey(byte[] vk)
    {
        _vk = vk;
        _encKey = Derive(vk, "privatepaste v1 enc", 32);
        _hashKey = Derive(vk, "privatepaste v1 hash", 32);
        var id = Derive(vk, "privatepaste v1 key id", 32);
        KeyId = Convert.ToHexStringLower(id.AsSpan(0, 8));
    }

    public static VaultKey Create() => new(RandomNumberGenerator.GetBytes(Length));

    public static VaultKey FromBytes(ReadOnlySpan<byte> vk)
    {
        if (vk.Length != Length)
            throw new ArgumentException($"A vault key is {Length} bytes.", nameof(vk));
        return new VaultKey(vk.ToArray());
    }

    /// <summary>Identifies the key without revealing it, so devices can tell whether they hold the server's current vault.</summary>
    public string KeyId { get; }

    /// <summary>A copy of the raw key; the caller owns it and should zero it when done.</summary>
    public byte[] Export() => (byte[])Live(_vk).Clone();

    internal byte[] EncKey => Live(_encKey);
    internal byte[] HashKey => Live(_hashKey);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_vk);
        CryptographicOperations.ZeroMemory(_encKey);
        CryptographicOperations.ZeroMemory(_hashKey);
    }

    private byte[] Live(byte[] key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return key;
    }

    private static byte[] Derive(byte[] vk, string info, int length) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, vk, length, salt: [], info: Encoding.UTF8.GetBytes(info));
}
