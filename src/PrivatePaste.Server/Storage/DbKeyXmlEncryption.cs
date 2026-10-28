using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using PrivatePaste.Server.Config;

namespace PrivatePaste.Server.Storage;

/// <summary>
/// Encrypts the persisted ASP.NET Core Data Protection keys (which protect the web session cookie) with AES-256-GCM
/// under a key derived from <c>PP_DB_KEY</c>, so they are not stored in plaintext under <c>/data/dp-keys</c>.
/// </summary>
public sealed class DbKeyXmlEncryptor(ServerOptions options) : IXmlEncryptor
{
    internal const string ElementName = "dbKeyEncryptedKey";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    internal static byte[] DeriveKey(ServerOptions options) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(options.DbKey), 32,
            info: Encoding.UTF8.GetBytes("privatepaste-dataprotection-v1"));

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var blob = new byte[NonceSize + TagSize + plaintext.Length];
        RandomNumberGenerator.Fill(blob.AsSpan(0, NonceSize));
        using (var aes = new AesGcm(DeriveKey(options), TagSize))
            aes.Encrypt(blob.AsSpan(0, NonceSize), plaintext, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize));
        CryptographicOperations.ZeroMemory(plaintext);

        var element = new XElement(ElementName,
            new XComment(" AES-256-GCM, key derived from PP_DB_KEY "),
            new XElement("value", Convert.ToBase64String(blob)));
        return new EncryptedXmlInfo(element, typeof(DbKeyXmlDecryptor));
    }

    internal static XElement Decrypt(ServerOptions options, XElement encryptedElement)
    {
        var blob = Convert.FromBase64String((string?)encryptedElement.Element("value")
            ?? throw new CryptographicException("Encrypted Data Protection key has no value."));
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted Data Protection key is truncated.");
        var plaintext = new byte[blob.Length - NonceSize - TagSize];
        using (var aes = new AesGcm(DeriveKey(options), TagSize))
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plaintext);
        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>Counterpart of <see cref="DbKeyXmlEncryptor"/>; created by Data Protection through its IServiceProvider constructor.</summary>
public sealed class DbKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement) =>
        DbKeyXmlEncryptor.Decrypt(services.GetRequiredService<ServerOptions>(), encryptedElement);
}
