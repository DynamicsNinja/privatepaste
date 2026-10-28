using System.Security.Cryptography;

namespace PrivatePaste.Client.Core.Security;

/// <summary>
/// AES-256-GCM with a key supplied by the OS (macOS keychain, Linux Secret Service or a mode-600 key file).
/// Layout: version(1) | nonce(12) | tag(16) | ciphertext. The key provider is called per operation and is expected to cache.
/// </summary>
public class AesGcmSecretProtector(Func<byte[]> keyProvider) : ISecretProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public byte[] Protect(byte[] plaintext)
    {
        var key = keyProvider();
        var result = new byte[1 + NonceSize + TagSize + plaintext.Length];
        result[0] = Version;
        var nonce = result.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, result.AsSpan(1 + NonceSize + TagSize), result.AsSpan(1 + NonceSize, TagSize), [Version]);
        return result;
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length < 1 + NonceSize + TagSize || protectedData[0] != Version)
            throw new CryptographicException("Not a PrivatePaste protected blob.");
        var key = keyProvider();
        var cipher = protectedData.AsSpan(1 + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(protectedData.AsSpan(1, NonceSize), cipher, protectedData.AsSpan(1 + NonceSize, TagSize), plain, [Version]);
        return plain;
    }
}
