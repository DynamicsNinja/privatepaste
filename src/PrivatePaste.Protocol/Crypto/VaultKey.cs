using System.Security.Cryptography;
using System.Text;

namespace PrivatePaste.Protocol.Crypto;

/// <summary>
/// The per-server vault key (VK) and the subkeys every client derives from it. The server never sees any of these.
/// <para>
/// Users never touch the live arrays: <see cref="Export"/>, <see cref="EncKey"/> and <see cref="HashKey"/> hand out a
/// copy taken under the lock <see cref="Dispose"/> takes, so a dispose that races a seal or an open either comes first
/// (the use throws <see cref="ObjectDisposedException"/>) or after the copy (the use finishes with the real key). No
/// crypto ever runs on wiped bytes.
/// </para>
/// </summary>
public sealed class VaultKey : IDisposable
{
    public const int Length = 32;

    private readonly byte[] _vk;
    private readonly byte[] _encKey;
    private readonly byte[] _hashKey;
    private readonly object _gate = new();
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
    public byte[] Export() => CopyOf(_vk);

    /// <summary>A copy of the encryption subkey; the caller zeroes it when done.</summary>
    internal byte[] EncKey => CopyOf(_encKey);

    /// <summary>A copy of the hash subkey; the caller zeroes it when done.</summary>
    internal byte[] HashKey => CopyOf(_hashKey);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_vk);
            CryptographicOperations.ZeroMemory(_encKey);
            CryptographicOperations.ZeroMemory(_hashKey);
        }
    }

    private byte[] CopyOf(byte[] key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (byte[])key.Clone();
        }
    }

    private static byte[] Derive(byte[] vk, string info, int length) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, vk, length, salt: [], info: Encoding.UTF8.GetBytes(info));
}
