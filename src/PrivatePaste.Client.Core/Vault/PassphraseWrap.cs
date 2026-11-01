using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Konscious.Security.Cryptography;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;

namespace PrivatePaste.Client.Core.Vault;

/// <summary>Thrown by <see cref="PassphraseWrap.Unwrap"/> when the wrap can't be opened with the passphrase.</summary>
public sealed class WrongPassphraseException(string message) : CryptographicException(message);

/// <summary>
/// Thrown by <see cref="PassphraseWrap.Unwrap"/> when the wrap isn't one this client accepts (malformed, another algorithm,
/// or Argon2 settings outside the bounds), whatever the passphrase: the user is told to update, not to retype it.
/// </summary>
public sealed class UnsupportedWrapException(string message) : CryptographicException(message);

/// <summary>
/// The vault key wrapped under a passphrase, so a new device can recover it: Argon2id stretches the (NFC-normalised)
/// passphrase into a key that seals the vault key with AES-256-GCM. The server stores the wrap but can't open it.
/// </summary>
public static partial class PassphraseWrap
{
    public const int MinLength = 12;
    public const string RuleMessage = "Use at least 12 characters. A short sentence works well.";
    public const string CommonMessage = "That passphrase is too common. Pick another.";

    private const string Alg = "argon2id";
    private const int WrapMemoryKiB = 65536, WrapIterations = 3, WrapParallelism = 1;
    private const int SaltSize = 16, NonceSize = 12, KeySize = 32, TagSize = 16;
    // Project-wide bounds on what a wrap may ask a client to spend, so a hostile server can't demand gigabytes.
    private const int MinMemoryKiB = 65536, MaxMemoryKiB = 262144;
    private const int MinIterations = 3, MaxIterations = 10;
    private const int MinParallelism = 1, MaxParallelism = 8;

    private static readonly Lazy<string[]> CommonLines = new(ReadListLines);
    private static readonly Lazy<HashSet<string>> Common = new(() => CommonLines.Value
        .Select(l => l.Normalize(NormalizationForm.FormC).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal));

    /// <summary>Same rule as the web page: length is the UTF-16 length of the NFC form, untrimmed.</summary>
    public static string? Problem(string passphrase)
    {
        var normalized = passphrase.Normalize(NormalizationForm.FormC);
        if (normalized.Length < MinLength) return RuleMessage;
        return Common.Value.Contains(normalized.ToLowerInvariant()) ? CommonMessage : null;
    }

    public static VaultWrapDto Wrap(VaultKey key, string passphrase) =>
        Wrap(key, passphrase, RandomNumberGenerator.GetBytes(SaltSize), RandomNumberGenerator.GetBytes(NonceSize));

    // Salt and nonce are parameters so the test vectors can pin them.
    internal static VaultWrapDto Wrap(VaultKey key, string passphrase, byte[] salt, byte[] nonce)
    {
        var kek = DeriveKek(passphrase, salt, WrapMemoryKiB, WrapIterations, WrapParallelism);
        var vk = key.Export();
        try
        {
            var wrapped = new byte[KeySize];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(kek, TagSize))
                aes.Encrypt(nonce, vk, wrapped, tag, Aad(key.KeyId));
            return new VaultWrapDto(key.KeyId,
                new VaultKdfDto(Alg, WrapMemoryKiB, WrapIterations, WrapParallelism, Convert.ToBase64String(salt)),
                Convert.ToBase64String(nonce), Convert.ToBase64String([.. wrapped, .. tag]));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(vk);
        }
    }

    /// <exception cref="WrongPassphraseException">The passphrase doesn't open the wrap.</exception>
    /// <exception cref="UnsupportedWrapException">The wrap is malformed or outside the accepted Argon2 bounds (checked before any key derivation).</exception>
    public static VaultKey Unwrap(VaultWrapDto wrap, string passphrase)
    {
        var kdf = wrap.Kdf;
        if (kdf is not { Alg: Alg }
            || wrap.KeyId is null || !KeyIdPattern().IsMatch(wrap.KeyId)
            || kdf.MemoryKiB is < MinMemoryKiB or > MaxMemoryKiB
            || kdf.Iterations is < MinIterations or > MaxIterations
            || kdf.Parallelism is < MinParallelism or > MaxParallelism
            || !TryDecode(kdf.Salt, SaltSize, out var salt)
            || !TryDecode(wrap.Nonce, NonceSize, out var nonce)
            || !TryDecode(wrap.WrappedKey, KeySize + TagSize, out var wrapped))
            throw new UnsupportedWrapException("Unsupported passphrase wrap.");

        var kek = DeriveKek(passphrase, salt, kdf.MemoryKiB, kdf.Iterations, kdf.Parallelism);
        var vk = new byte[KeySize];
        try
        {
            try
            {
                using var aes = new AesGcm(kek, TagSize);
                aes.Decrypt(nonce, wrapped.AsSpan(0, KeySize), wrapped.AsSpan(KeySize), vk, Aad(wrap.KeyId));
            }
            catch (CryptographicException)
            {
                throw new WrongPassphraseException("Wrong passphrase.");
            }
            var key = VaultKey.FromBytes(vk);
            if (key.KeyId == wrap.KeyId) return key;
            key.Dispose();
            throw new WrongPassphraseException("The wrap holds a different vault key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(vk);
        }
    }

    internal static byte[] DeriveKek(string passphrase, byte[] salt, int memoryKiB, int iterations, int parallelism)
    {
        var password = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
        try
        {
            using var argon = new Argon2id(password)
            {
                Salt = salt,
                MemorySize = memoryKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };
            return argon.GetBytes(KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    /// <summary>The first entry of the embedded list, as written in the file.</summary>
    internal static string FirstCommonEntry() => CommonLines.Value[0];

    internal static Stream OpenCommonList() =>
        typeof(PassphraseWrap).Assembly.GetManifestResourceStream("common-passphrases.txt")
        ?? throw new InvalidOperationException("The common-passphrase list is missing from the build.");

    private static byte[] Aad(string keyId) => Encoding.UTF8.GetBytes("privatepaste v1 vault " + keyId);

    private static bool TryDecode(string? text, int length, out byte[] bytes)
    {
        bytes = [];
        if (text is null) return false;
        var buffer = new byte[length + 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written) || written != length) return false;
        bytes = buffer[..length];
        return true;
    }

    private static string[] ReadListLines()
    {
        using var reader = new StreamReader(OpenCommonList(), Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && !line.StartsWith('#'))
                lines.Add(line);
        return [.. lines];
    }

    [GeneratedRegex(@"^[0-9a-f]{16}\z")]
    private static partial Regex KeyIdPattern();
}
