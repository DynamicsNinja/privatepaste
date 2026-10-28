using System.Security.Cryptography;
using System.Text;
using PrivatePaste.Server.Config;

namespace PrivatePaste.Server.Storage;

/// <summary>AES-256-GCM encrypted blob files: nonce(12) || tag(16) || ciphertext, stored as {name}.enc.</summary>
public sealed class BlobStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _dir;
    private readonly byte[] _key;

    public BlobStore(ServerOptions options)
    {
        _dir = Path.Combine(options.DataDir, "blobs");
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(options.DbKey), 32,
            info: Encoding.UTF8.GetBytes("privatepaste-blobs-v1"));
    }

    public async Task WriteAsync(string name, byte[] plaintext)
    {
        Directory.CreateDirectory(_dir);
        var file = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = file.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize));
        await File.WriteAllBytesAsync(PathFor(name), file);
    }

    public async Task<byte[]?> ReadAsync(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        byte[] file;
        try { file = await File.ReadAllBytesAsync(path); }
        catch (FileNotFoundException) { return null; }
        if (file.Length < NonceSize + TagSize) throw new CryptographicException("Blob file is truncated.");
        var plaintext = new byte[file.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(file.AsSpan(0, NonceSize), file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize), plaintext);
        return plaintext;
    }

    public void Delete(string name) => File.Delete(PathFor(name));

    private string PathFor(string name)
    {
        if (name.Length == 0 || name.AsSpan().IndexOfAny('/', (char)92) >= 0 || name.Contains(".."))
            throw new ArgumentException("Invalid blob name.", nameof(name));
        return Path.Combine(_dir, name + ".enc");
    }
}
