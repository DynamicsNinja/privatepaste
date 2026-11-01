using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PrivatePaste.Protocol.Crypto;

/// <summary>
/// Moves the vault key to a new device through a QR code: the server holds only the sealed blob, and the one-time
/// key that opens it travels in the QR text, so neither side alone can recover the vault key.
/// </summary>
public static partial class Handover
{
    private const string QrPrefix = "privatepaste:handover?";
    private const int OtkSize = 32;
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("privatepaste v1 handover");

    public static (byte[] Otk, byte[] Blob) Wrap(VaultKey key)
    {
        var otk = RandomNumberGenerator.GetBytes(OtkSize);
        return (otk, Wrap(key, otk, RandomNumberGenerator.GetBytes(12)));
    }

    internal static byte[] Wrap(VaultKey key, ReadOnlySpan<byte> otk, ReadOnlySpan<byte> nonce)
    {
        var vk = key.Export();
        try { return SealedBox.Seal(otk, Aad, vk, nonce); }
        finally { CryptographicOperations.ZeroMemory(vk); }
    }

    /// <exception cref="CryptographicException">The one-time key is wrong, the blob was tampered with, or the
    /// key inside isn't the one the QR code promised.</exception>
    public static VaultKey Unwrap(ReadOnlySpan<byte> otk, ReadOnlySpan<byte> blob, string expectedKeyId)
    {
        if (otk.Length != OtkSize)
            throw new CryptographicException("The handover key is the wrong length.");
        var vk = SealedBox.Open(otk, Aad, blob);
        try
        {
            if (vk.Length != VaultKey.Length)
                throw new CryptographicException("The handover holds no vault key.");
            var key = VaultKey.FromBytes(vk);
            if (key.KeyId == expectedKeyId) return key;
            key.Dispose();
            throw new CryptographicException("The handover holds a different vault key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vk);
        }
    }

    public static string QrText(Guid id, ReadOnlySpan<byte> otk, string keyId) =>
        $"{QrPrefix}id={id:D}&k={Base64Url.EncodeToString(otk)}&kid={keyId}";

    public static bool TryParseQr(string text, out Guid id, out byte[] otk, out string keyId)
    {
        id = Guid.Empty;
        otk = [];
        keyId = "";
        if (!text.StartsWith(QrPrefix, StringComparison.Ordinal))
            return false;

        string? idText = null, kText = null, kidText = null;
        foreach (var pair in text[QrPrefix.Length..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var value = pair[(eq + 1)..];
            // A repeated field is ambiguous, so the whole code is rejected; unknown fields are left for later versions.
            switch (pair[..eq])
            {
                case "id" when idText is null: idText = value; break;
                case "k" when kText is null: kText = value; break;
                case "kid" when kidText is null: kidText = value; break;
                case "id" or "k" or "kid": return false;
            }
        }

        if (!Guid.TryParseExact(idText, "D", out var parsedId) || kText is null || kidText is null || !KeyIdPattern().IsMatch(kidText))
            return false;
        byte[] parsedOtk;
        try { parsedOtk = Base64Url.DecodeFromChars(kText); }
        catch (FormatException) { return false; }
        // Only the canonical spelling is accepted (no padding, whitespace or stray trailing bits), so every client agrees.
        if (parsedOtk.Length != OtkSize || Base64Url.EncodeToString(parsedOtk) != kText)
            return false;

        id = parsedId;
        otk = parsedOtk;
        keyId = kidText;
        return true;
    }

    [GeneratedRegex(@"^[0-9a-f]{16}\z")]
    private static partial Regex KeyIdPattern();
}
