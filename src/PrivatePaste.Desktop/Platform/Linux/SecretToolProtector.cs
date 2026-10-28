using System.ComponentModel;
using System.Security.Cryptography;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Desktop.Platform.Unix;

namespace PrivatePaste.Desktop.Platform.Linux;

/// <summary>
/// A random 32-byte key lives in the Secret Service via <c>secret-tool store/lookup --label=PrivatePaste service privatepaste key data</c>
/// (the secret goes through stdin, never argv). Without <c>secret-tool</c>, or when a clean not-found lookup is followed by a failing store, the key is kept in a
/// mode-600 file next to the app data, with a one-time toast: that protects against other users, not against other
/// processes of the same user. Any other lookup failure (timeout, locked keyring) throws instead: the app must not replace a key it merely cannot reach.
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
        private InvalidOperationException? _failure;

        /// <summary>Success and failure are both decided once per session, so a hung or locked keyring never blocks twice.</summary>
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
                        ?? new InvalidOperationException("The Linux secret store is not available: " + e.Message, e);
                    throw _failure;
                }
            }
        }

        private byte[] LoadOrCreate()
        {
            // An existing key file means an earlier run had no keyring; keep using it (the data was protected with it).
            if (TryReadKeyFile(out var fileKey)) return fileKey;

            CommandResult found;
            try
            {
                found = runner.Run("secret-tool", ["lookup", .. Attributes], null);
            }
            catch (Win32Exception)
            {
                return CreateKeyFile();   // secret-tool is not installed
            }
            // A timeout propagates: a keyring key may well exist, and a key file would permanently supersede it.

            if (found.ExitCode == 0 && TryDecode(found.Stdout, out var existing)) return existing;

            // Not found is exit 1 with nothing on stdout or stderr. Any other lookup outcome (D-Bus error, locked keyring,
            // unreadable value) means a key may exist but is unreachable: never store, never create a key file. Restart
            // after unlocking the keyring.
            var notFound = found.ExitCode == 1 && found.Stdout.Length == 0 && string.IsNullOrWhiteSpace(found.Stderr);
            if (!notFound)
                throw new InvalidOperationException(
                    $"Could not read the key from the Secret Service (secret-tool exit code {found.ExitCode}). Unlock the keyring and restart PrivatePaste.");

            var key = RandomNumberGenerator.GetBytes(32);
            var store = runner.Run("secret-tool", ["store", "--label=PrivatePaste", .. Attributes], Convert.ToBase64String(key));
            return store.ExitCode == 0 ? key : CreateKeyFile();   // nothing stored, so nothing to supersede
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
