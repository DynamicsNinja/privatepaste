using System.Security.Cryptography;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Desktop.Platform.Unix;

namespace PrivatePaste.Desktop.Platform.Mac;

/// <summary>
/// A random 32-byte key lives in the login keychain (service <c>PrivatePaste</c>, account <c>data-key</c>); the data is
/// AES-GCM protected with it. The key is added through <c>security -i</c> (commands read from stdin) so the base64 key
/// never shows up in the process list; <c>find-generic-password -w</c> only prints to our pipe.
/// </summary>
public sealed class KeychainSecretProtector : AesGcmSecretProtector
{
    private const string Service = "PrivatePaste";
    private const string Account = "data-key";

    public KeychainSecretProtector() : this(new ProcessCommandRunner()) { }

    public KeychainSecretProtector(ICommandRunner runner) : base(new KeyCache(runner).Get) { }

    private sealed class KeyCache(ICommandRunner runner)
    {
        private const int ItemNotFound = 44;   // errSecItemNotFound as reported by `security`

        private readonly object _gate = new();
        private byte[]? _key;
        private InvalidOperationException? _failure;

        /// <summary>Success and failure are both decided once per session, so a hung or locked keychain never blocks twice.</summary>
        public byte[] Get()
        {
            lock (_gate)
            {
                if (_key is not null) return _key;
                if (_failure is not null) throw _failure;
                try
                {
                    return _key = LoadOrCreate();
                }
                catch (Exception e)
                {
                    _failure = e as InvalidOperationException
                        ?? new InvalidOperationException("The macOS keychain is not available: " + e.Message, e);
                    throw _failure;
                }
            }
        }

        private (int ExitCode, byte[]? Key) Find()
        {
            var found = runner.Run("security", ["find-generic-password", "-s", Service, "-a", Account, "-w"], null);
            return (found.ExitCode, found.ExitCode == 0 && TryDecode(found.Stdout, out var key) ? key : null);
        }

        private byte[] LoadOrCreate()
        {
            var (exitCode, existing) = Find();
            if (existing is not null) return existing;
            // Create only when the item is genuinely absent. A locked keychain, a denied prompt or an unreadable value
            // must never lead to a new key: that would orphan everything protected with the old one.
            if (exitCode != ItemNotFound)
                throw new InvalidOperationException($"Could not read the key from the macOS keychain (security exit code {exitCode}).");

            var key = RandomNumberGenerator.GetBytes(32);
            var add = runner.Run("security", ["-i"],
                $"add-generic-password -s {Service} -a {Account} -w {Convert.ToBase64String(key)} -U\n");
            if (add.ExitCode != 0) throw new InvalidOperationException("Could not store the key in the macOS keychain.");

            // `security -i` can exit 0 even when a command inside failed: confirm the key really persisted.
            var (_, stored) = Find();
            if (stored is null || !stored.AsSpan().SequenceEqual(key))
                throw new InvalidOperationException("The macOS keychain did not keep the new key.");
            return key;
        }

        private static bool TryDecode(string text, out byte[] key)
        {
            key = [];
            try { key = Convert.FromBase64String(text.Trim()); }
            catch (FormatException) { return false; }
            return key.Length == 32;
        }
    }
}
