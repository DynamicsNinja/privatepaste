using System.Security.Cryptography;
using System.Text;

namespace PrivatePaste.Protocol.Crypto;

/// <summary>
/// The small-item format: <c>0x01 || nonce(12) || ciphertext || tag(16)</c>, AES-256-GCM with associated data
/// <c>"privatepaste v1 " + purpose</c>. The purpose in the associated data stops a thumbnail being served as a payload.
/// </summary>
public static class SealedBox
{
    private const byte Version = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    internal const int Overhead = 1 + NonceSize + TagSize;

    public static byte[] Seal(VaultKey key, string purpose, ReadOnlySpan<byte> plaintext) =>
        Seal(key.EncKey, Aad(purpose), plaintext, RandomNumberGenerator.GetBytes(NonceSize));

    /// <exception cref="CryptographicException">The input is malformed, was tampered with, or was sealed under another key or purpose.</exception>
    public static byte[] Open(VaultKey key, string purpose, ReadOnlySpan<byte> sealedBytes) =>
        Open(key.EncKey, Aad(purpose), sealedBytes);

    internal static byte[] Seal(ReadOnlySpan<byte> aesKey, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceSize)
            throw new ArgumentException($"The nonce is {NonceSize} bytes.", nameof(nonce));
        var output = new byte[Overhead + plaintext.Length];
        output[0] = Version;
        nonce.CopyTo(output.AsSpan(1));
        using var aes = new AesGcm(aesKey, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(1 + NonceSize, plaintext.Length), output.AsSpan(^TagSize), aad);
        return output;
    }

    internal static byte[] Open(ReadOnlySpan<byte> aesKey, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < Overhead || sealedBytes[0] != Version)
            throw new CryptographicException("Not a sealed item.");
        var plaintext = new byte[sealedBytes.Length - Overhead];
        using var aes = new AesGcm(aesKey, TagSize);
        aes.Decrypt(sealedBytes.Slice(1, NonceSize), sealedBytes[(1 + NonceSize)..^TagSize], sealedBytes[^TagSize..], plaintext, aad);
        return plaintext;
    }

    // Only vault purposes go through the public API: "blob" has its own format and "handover" its own key.
    private static byte[] Aad(string purpose) => purpose is "payload" or "thumb"
        ? Encoding.UTF8.GetBytes("privatepaste v1 " + purpose)
        : throw new ArgumentException($"Unknown purpose '{purpose}'.", nameof(purpose));
}
