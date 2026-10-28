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
        private readonly object _gate = new();
        private byte[]? _key;

        public byte[] Get()
        {
            lock (_gate) return _key ??= LoadOrCreate();
        }

        private byte[] LoadOrCreate()
        {
            var found = runner.Run("security", ["find-generic-password", "-s", Service, "-a", Account, "-w"], null);
            if (found.ExitCode == 0 && TryDecode(found.Stdout, out var existing)) return existing;

            var key = RandomNumberGenerator.GetBytes(32);
            var add = runner.Run("security", ["-i"],
                $"add-generic-password -s {Service} -a {Account} -w {Convert.ToBase64String(key)} -U\n");
            if (add.ExitCode != 0) throw new InvalidOperationException("Could not store the key in the macOS keychain.");
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
