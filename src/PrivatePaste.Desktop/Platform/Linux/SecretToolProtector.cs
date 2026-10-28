using System.ComponentModel;
using System.Security.Cryptography;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Desktop.Platform.Unix;

namespace PrivatePaste.Desktop.Platform.Linux;

/// <summary>
/// A random 32-byte key lives in the Secret Service via <c>secret-tool store/lookup --label=PrivatePaste service privatepaste key data</c>
/// (the secret goes through stdin, never argv). Without <c>secret-tool</c> (or a usable keyring) the key is kept in a
/// mode-600 file next to the app data, with a one-time toast: that protects against other users, not against other
/// processes of the same user.
/// </summary>
public sealed class SecretToolProtector : AesGcmSecretProtector
{
    public SecretToolProtector(string fallbackKeyFile, INotifier notifier)
        : this(new ProcessCommandRunner(), fallbackKeyFile, notifier) { }

    public SecretToolProtector(ICommandRunner runner, string fallbackKeyFile, INotifier notifier)
        : base(new KeyCache(runner, fallbackKeyFile, notifier).Get) { }

    private sealed class KeyCache(ICommandRunner runner, string keyFile, INotifier notifier)
    {
        private static readonly string[] Attributes = ["service", "privatepaste", "key", "data"];
        private readonly object _gate = new();
        private byte[]? _key;

        public byte[] Get()
        {
            lock (_gate) return _key ??= LoadOrCreate();
        }

        private byte[] LoadOrCreate()
        {
            // An existing key file means an earlier run had no keyring; keep using it (the data was protected with it).
            if (TryReadKeyFile(out var fileKey)) return fileKey;
            try
            {
                var found = runner.Run("secret-tool", ["lookup", .. Attributes], null);
                if (found.ExitCode == 0 && TryDecode(found.Stdout, out var existing)) return existing;

                // Not found is exit 1 with nothing on stdout or stderr. Anything else (D-Bus errors, a locked or missing
                // keyring, an unreadable value) must not trigger a store: it could replace a key that is merely unreachable.
                var notFound = found.ExitCode == 1 && found.Stdout.Length == 0 && string.IsNullOrWhiteSpace(found.Stderr);
                if (notFound)
                {
                    var key = RandomNumberGenerator.GetBytes(32);
                    var store = runner.Run("secret-tool", ["store", "--label=PrivatePaste", .. Attributes], Convert.ToBase64String(key));
                    if (store.ExitCode == 0) return key;
                }
            }
            catch (Win32Exception)
            {
                // secret-tool is not installed.
            }
            catch (TimeoutException)
            {
                // Hung (no keyring daemon to answer): use the key file. The result is cached, so this costs one timeout per session.
            }
            return CreateKeyFile();
        }

        private bool TryReadKeyFile(out byte[] key)
        {
            key = [];
            if (!File.Exists(keyFile)) return false;
            key = File.ReadAllBytes(keyFile);
            return key.Length == 32;
        }

        private byte[] CreateKeyFile()
        {
            var key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(keyFile, options)) stream.Write(key);
            notifier.Notify("Secret storage (secret-tool from libsecret-tools) was not found; the token is protected with a key file in your config folder instead.");
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
