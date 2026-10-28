using System.Security.Cryptography;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Desktop.Platform.Linux;
using PrivatePaste.Desktop.Platform.Mac;
using PrivatePaste.Desktop.Platform.Unix;

namespace PrivatePaste.Tests.Client;

public class UnixSecretProtectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-unixsecret-" + Guid.NewGuid().ToString("N"));
    public UnixSecretProtectorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class FakeRunner : ICommandRunner
    {
        public List<(string File, string[] Args, string? Stdin)> Calls { get; } = [];
        public Func<string, string[], string?, CommandResult> Handler { get; set; } = (_, _, _) => new CommandResult(0, "");
        public CommandResult Run(string file, string[] args, string? stdin)
        {
            Calls.Add((file, args, stdin));
            return Handler(file, args, stdin);
        }
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<string> Messages { get; } = [];
        public void Notify(string message) => Messages.Add(message);
    }

    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    // ---- AES-GCM envelope ----

    [Fact]
    public void AesGcm_roundtrips_and_is_not_plaintext()
    {
        var key = Key();
        var p = new AesGcmSecretProtector(() => key);
        var data = "tok-123"u8.ToArray();
        var boxed = p.Protect(data);
        Assert.DoesNotContain("tok-123", System.Text.Encoding.UTF8.GetString(boxed));
        Assert.Equal(data, p.Unprotect(boxed));
    }

    [Fact]
    public void AesGcm_uses_a_fresh_nonce_each_time()
    {
        var k = Key();
        var p = new AesGcmSecretProtector(() => k);
        Assert.NotEqual(p.Protect([1, 2, 3]), p.Protect([1, 2, 3]));
    }

    [Fact]
    public void AesGcm_rejects_tampering_wrong_key_and_short_input()
    {
        var k = Key();
        var p = new AesGcmSecretProtector(() => k);
        var boxed = p.Protect([1, 2, 3, 4]);
        var tampered = (byte[])boxed.Clone();
        tampered[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(tampered));
        var other = new AesGcmSecretProtector(Key);
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(boxed));
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect([1, 2]));
    }

    // ---- macOS keychain ----

    [Fact]
    public void Keychain_creates_key_when_missing_and_passes_secret_via_stdin_not_argv()
    {
        var runner = new FakeRunner
        {
            Handler = (_, args, _) => args[0] == "find-generic-password" ? new CommandResult(44, "") : new CommandResult(0, ""),
        };
        var p = new KeychainSecretProtector(runner);
        p.Protect([1]);
        var add = Assert.Single(runner.Calls, c => c.Args[0] == "-i");
        Assert.Equal("security", add.File);
        Assert.StartsWith("add-generic-password -s PrivatePaste -a data-key -w ", add.Stdin);
        Assert.EndsWith(" -U", add.Stdin!.TrimEnd());
        Assert.DoesNotContain(runner.Calls, c => c.Args.Contains("-w") && c.Args[0] != "find-generic-password");
    }

    [Fact]
    public void Keychain_reuses_stored_key_across_instances()
    {
        string? stored = null;
        var runner = new FakeRunner
        {
            Handler = (_, args, stdin) =>
            {
                if (args[0] == "find-generic-password") return stored is null ? new CommandResult(44, "") : new CommandResult(0, stored + "\n");
                var parts = stdin!.Split(' ');
                stored = parts[Array.IndexOf(parts, "-w") + 1];
                return new CommandResult(0, "");
            },
        };
        var boxed = new KeychainSecretProtector(runner).Protect("abc"u8.ToArray());
        Assert.Equal("abc"u8.ToArray(), new KeychainSecretProtector(runner).Unprotect(boxed));
    }

    [Fact]
    public void Keychain_find_and_add_failure_throws()
    {
        var runner = new FakeRunner { Handler = (_, _, _) => new CommandResult(1, "") };
        Assert.Throws<InvalidOperationException>(() => new KeychainSecretProtector(runner).Protect([1]));
    }

    // ---- Linux secret-tool ----

    [Fact]
    public void SecretTool_stores_the_key_via_stdin_with_the_documented_attributes()
    {
        var runner = new FakeRunner
        {
            Handler = (_, args, _) => args[0] == "lookup" ? new CommandResult(1, "") : new CommandResult(0, ""),
        };
        var p = new SecretToolProtector(runner, Path.Combine(_dir, "key"), new FakeNotifier());
        p.Protect([1]);
        var store = Assert.Single(runner.Calls, c => c.Args[0] == "store");
        Assert.Equal("secret-tool", store.File);
        Assert.Equal(["store", "--label=PrivatePaste", "service", "privatepaste", "key", "data"], store.Args);
        Assert.False(string.IsNullOrWhiteSpace(store.Stdin));
        Assert.Equal(["lookup", "service", "privatepaste", "key", "data"], runner.Calls[0].Args);
        Assert.False(File.Exists(Path.Combine(_dir, "key")));
    }

    [Fact]
    public void SecretTool_reuses_stored_key()
    {
        string? stored = null;
        var runner = new FakeRunner
        {
            Handler = (_, args, stdin) =>
            {
                if (args[0] == "lookup") return stored is null ? new CommandResult(1, "") : new CommandResult(0, stored);
                stored = stdin;
                return new CommandResult(0, "");
            },
        };
        var file = Path.Combine(_dir, "key");
        var boxed = new SecretToolProtector(runner, file, new FakeNotifier()).Protect("abc"u8.ToArray());
        Assert.Equal("abc"u8.ToArray(), new SecretToolProtector(runner, file, new FakeNotifier()).Unprotect(boxed));
    }

    [Fact]
    public void SecretTool_missing_falls_back_to_key_file_and_toasts_once()
    {
        var runner = new FakeRunner { Handler = (_, _, _) => throw new System.ComponentModel.Win32Exception("not found") };
        var file = Path.Combine(_dir, "sub", "key");
        var notifier = new FakeNotifier();
        var boxed = new SecretToolProtector(runner, file, notifier).Protect("abc"u8.ToArray());
        Assert.True(File.Exists(file));
        Assert.Equal(32, File.ReadAllBytes(file).Length);
        Assert.Single(notifier.Messages);
        Assert.Contains("secret-tool", notifier.Messages[0]);

        // A later run finds the existing key file: same key, no second toast.
        var again = new FakeNotifier();
        Assert.Equal("abc"u8.ToArray(), new SecretToolProtector(runner, file, again).Unprotect(boxed));
        Assert.Empty(again.Messages);
    }

    [Fact]
    public void SecretTool_fallback_key_file_is_mode_600()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new FakeRunner { Handler = (_, _, _) => throw new System.ComponentModel.Win32Exception("not found") };
        var file = Path.Combine(_dir, "key");
        new SecretToolProtector(runner, file, new FakeNotifier()).Protect([1]);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void SecretFile_creates_files_with_mode_600_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "token.bin");
        var key = Key();
        new SecretFile(path, new AesGcmSecretProtector(() => key)).Write("tok");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
