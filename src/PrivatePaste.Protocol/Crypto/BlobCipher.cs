using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PrivatePaste.Protocol.Crypto;

/// <summary>
/// Streaming encryption for large items: header <c>0x02 || noncePrefix(8)</c>, then 1 MiB plaintext chunks, each
/// <c>ciphertext || tag(16)</c> with nonce <c>noncePrefix || uint32BE(index)</c>. The index and an is-final flag in
/// the associated data make reordered, dropped or appended chunks fail authentication.
/// </summary>
public static class BlobCipher
{
    public const int ChunkSize = 1048576;

    private const byte Version = 0x02;
    private const int PrefixSize = 8;
    private const int HeaderSize = 1 + PrefixSize;
    private const int TagSize = 16;
    private static readonly byte[] AadLabel = Encoding.UTF8.GetBytes("privatepaste v1 blob");

    public static Task EncryptAsync(VaultKey key, Stream plaintext, Stream output, CancellationToken ct) =>
        EncryptAsync(key, plaintext, output, RandomNumberGenerator.GetBytes(PrefixSize), ct);

    internal static async Task EncryptAsync(VaultKey key, Stream plaintext, Stream output, ReadOnlyMemory<byte> noncePrefix, CancellationToken ct)
    {
        if (noncePrefix.Length != PrefixSize)
            throw new ArgumentException($"The nonce prefix is {PrefixSize} bytes.", nameof(noncePrefix));
        var header = new byte[HeaderSize];
        header[0] = Version;
        noncePrefix.Span.CopyTo(header.AsSpan(1));
        await output.WriteAsync(header, ct);

        using var aes = new AesGcm(key.EncKey, TagSize);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var sealedChunk = new byte[ChunkSize + TagSize];
        try
        {
            // Reading one chunk ahead tells us whether the current one is final before it is sealed.
            var currentLength = await plaintext.ReadAtLeastAsync(current, ChunkSize, throwOnEndOfStream: false, ct);
            for (uint index = 0; ; index = checked(index + 1))
            {
                var nextLength = currentLength < ChunkSize ? 0 : await plaintext.ReadAtLeastAsync(next, ChunkSize, throwOnEndOfStream: false, ct);
                var isFinal = nextLength == 0;
                aes.Encrypt(Nonce(header, index), current.AsSpan(0, currentLength), sealedChunk.AsSpan(0, currentLength),
                    sealedChunk.AsSpan(currentLength, TagSize), Aad(index, isFinal));
                await output.WriteAsync(sealedChunk.AsMemory(0, currentLength + TagSize), ct);
                if (isFinal) return;
                (current, next) = (next, current);
                currentLength = nextLength;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
            CryptographicOperations.ZeroMemory(next);
        }
    }

    /// <summary>
    /// Decrypts chunk by chunk, writing each one only after it authenticates. An exception can still come after
    /// earlier chunks were written, so the caller must discard <paramref name="plaintext"/> on failure.
    /// </summary>
    /// <exception cref="CryptographicException">The blob is malformed, tampered with, truncated, reordered or has trailing data.</exception>
    public static async Task DecryptAsync(VaultKey key, Stream input, Stream plaintext, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        if (await input.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, ct) < HeaderSize || header[0] != Version)
            throw new CryptographicException("Not an encrypted blob.");

        using var aes = new AesGcm(key.EncKey, TagSize);
        var current = new byte[ChunkSize + TagSize];
        var next = new byte[ChunkSize + TagSize];
        var chunk = new byte[ChunkSize];
        try
        {
            var currentLength = await input.ReadAtLeastAsync(current, current.Length, throwOnEndOfStream: false, ct);
            for (uint index = 0; ; index = checked(index + 1))
            {
                if (currentLength < TagSize)
                    throw new CryptographicException("The blob is truncated.");
                // A short read means end of stream, so that chunk is the last; a full one is last only if nothing follows.
                var nextLength = currentLength < current.Length ? 0 : await input.ReadAtLeastAsync(next, next.Length, throwOnEndOfStream: false, ct);
                var isFinal = nextLength == 0;
                var length = currentLength - TagSize;
                aes.Decrypt(Nonce(header, index), current.AsSpan(0, length), current.AsSpan(length, TagSize),
                    chunk.AsSpan(0, length), Aad(index, isFinal));
                await plaintext.WriteAsync(chunk.AsMemory(0, length), ct);
                if (isFinal) return;
                (current, next) = (next, current);
                currentLength = nextLength;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
        }
    }

    private static byte[] Nonce(byte[] header, uint index)
    {
        var nonce = new byte[PrefixSize + 4];
        header.AsSpan(1, PrefixSize).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixSize), index);
        return nonce;
    }

    private static byte[] Aad(uint index, bool isFinal)
    {
        var aad = new byte[AadLabel.Length + 5];
        AadLabel.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(AadLabel.Length), index);
        aad[^1] = isFinal ? (byte)1 : (byte)0;
        return aad;
    }
}
