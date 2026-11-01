using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;
using PrivatePaste.Tests.Web;

namespace PrivatePaste.Tests.Client;

public class PassphraseWrapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-vault-" + Guid.NewGuid().ToString("N"));

    public PassphraseWrapTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] protectedData) => protectedData.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    private static JsonDocument Vectors() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(PlatformIconsTests.RepoRoot(), "docs", "e2e-test-vectors.json")));

    [Fact]
    public void Wrap_RoundTrips()
    {
        using var key = VaultKey.Create();
        var wrap = PassphraseWrap.Wrap(key, "my cat eats lemons daily");

        Assert.Equal(key.KeyId, wrap.KeyId);
        Assert.Equal(new VaultKdfDto("argon2id", 65536, 3, 1, wrap.Kdf.Salt), wrap.Kdf);
        Assert.Equal(16, Convert.FromBase64String(wrap.Kdf.Salt).Length);
        Assert.Equal(12, Convert.FromBase64String(wrap.Nonce).Length);
        Assert.Equal(48, Convert.FromBase64String(wrap.WrappedKey).Length);
        using var back = PassphraseWrap.Unwrap(wrap, "my cat eats lemons daily");
        Assert.Equal(key.Export(), back.Export());
    }

    [Fact]
    public void WrongPassphrase_Throws()
    {
        using var key = VaultKey.Create();
        var wrap = PassphraseWrap.Wrap(key, "my cat eats lemons daily");
        Assert.Throws<WrongPassphraseException>(() => PassphraseWrap.Unwrap(wrap, "my cat eats lemons nightly"));
        Assert.Throws<WrongPassphraseException>(() => PassphraseWrap.Unwrap(wrap with { KeyId = "0000000000000000" }, "my cat eats lemons daily"));
    }

    [Fact]
    public void NfdAndNfcUnlockTheSameWrap()
    {
        const string text = "Šećer na 5. katu";
        using var key = VaultKey.Create();
        var wrap = PassphraseWrap.Wrap(key, text.Normalize(NormalizationForm.FormC));
        using var back = PassphraseWrap.Unwrap(wrap, text.Normalize(NormalizationForm.FormD));
        Assert.Equal(key.KeyId, back.KeyId);
    }

    [Fact]
    public void Unwrap_RejectsUnacceptableWrapsBeforeDerivingAKey()
    {
        using var key = VaultKey.Create();
        var good = PassphraseWrap.Wrap(key, "my cat eats lemons daily");
        string B64(int n) => Convert.ToBase64String(new byte[n]);

        VaultWrapDto[] bad =
        [
            good with { Kdf = good.Kdf with { Alg = "argon2i" } },
            good with { Kdf = good.Kdf with { MemoryKiB = 65535 } },
            good with { Kdf = good.Kdf with { MemoryKiB = 262145 } },
            good with { Kdf = good.Kdf with { MemoryKiB = 1 << 30 } },
            good with { Kdf = good.Kdf with { Iterations = 2 } },
            good with { Kdf = good.Kdf with { Iterations = 11 } },
            good with { Kdf = good.Kdf with { Parallelism = 0 } },
            good with { Kdf = good.Kdf with { Parallelism = 9 } },
            good with { Kdf = good.Kdf with { Salt = B64(15) } },
            good with { Kdf = good.Kdf with { Salt = "not base64!" } },
            good with { Nonce = B64(11) },
            good with { WrappedKey = B64(47) },
            good with { KeyId = "ABCDEF0123456789" },
            good with { KeyId = "0123" },
        ];
        // Told apart from a wrong passphrase: the right passphrase can't open these either.
        foreach (var wrap in bad)
            Assert.Throws<UnsupportedWrapException>(() => PassphraseWrap.Unwrap(wrap, "my cat eats lemons daily"));
    }

    [Fact]
    public void Problem_RejectsShortAndCommon()
    {
        Assert.Equal(PassphraseWrap.RuleMessage, PassphraseWrap.Problem("short"));
        Assert.Equal(PassphraseWrap.RuleMessage, PassphraseWrap.Problem(""));
        // No trimming: twelve characters of padding count, as on the web.
        Assert.Null(PassphraseWrap.Problem("   short    "));
        Assert.Null(PassphraseWrap.Problem("my cat eats lemons daily"));
        var first = PassphraseWrap.FirstCommonEntry();
        Assert.Equal("That passphrase is too common. Pick another.", PassphraseWrap.Problem(first));
        Assert.Equal(PassphraseWrap.Problem(first), PassphraseWrap.Problem(first.ToUpperInvariant()));
        Assert.Equal(PassphraseWrap.Problem(first), PassphraseWrap.Problem(first.Normalize(NormalizationForm.FormD)));
    }

    [Fact]
    public void KeyStore_SaveLoadDelete()
    {
        var path = Path.Combine(_dir, "vault.bin");
        var store = new VaultKeyStore(path, new XorProtector());
        Assert.Null(store.Load());

        using var key = VaultKey.Create();
        store.Save(key);
        using (var loaded = store.Load())
        {
            Assert.NotNull(loaded);
            Assert.Equal(key.Export(), loaded.Export());
        }
        // The key is not on disk in the clear.
        Assert.DoesNotContain(Convert.ToBase64String(key.Export()), Encoding.UTF8.GetString(File.ReadAllBytes(path)));

        store.Delete();
        Assert.Null(store.Load());

        File.WriteAllBytes(path, new XorProtector().Protect("not a key"u8.ToArray()));
        Assert.Null(store.Load());
    }

    [Fact]
    public void CommonList_MatchesWebCopy()
    {
        using var embedded = PassphraseWrap.OpenCommonList();
        var web = File.ReadAllBytes(Path.Combine(PlatformIconsTests.RepoRoot(),
            "src", "PrivatePaste.Server", "wwwroot", "js", "common-passphrases.txt"));
        Assert.Equal(SHA256.HashData(web), SHA256.HashData(embedded));
    }

    [Fact]
    public void Argon2Vectors_Match()
    {
        using var doc = Vectors();
        var cases = doc.RootElement.GetProperty("argon2").EnumerateArray().ToArray();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var key = PassphraseWrap.DeriveKek(c.GetProperty("passphrase").GetString()!, FromHex(c.GetProperty("salt").GetString()!),
                c.GetProperty("memoryKiB").GetInt32(), c.GetProperty("iterations").GetInt32(), c.GetProperty("parallelism").GetInt32());
            Assert.Equal(c.GetProperty("key").GetString(), Convert.ToHexStringLower(key));
        }
    }

    [Fact]
    public void PassphraseWrapVectors_Unwrap()
    {
        using var doc = Vectors();
        var cases = doc.RootElement.GetProperty("passphraseWrap").EnumerateArray().ToArray();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var wrap = c.GetProperty("wrap").Deserialize<VaultWrapDto>(ProtocolJson.Options)!;
            var passphrase = c.GetProperty("passphrase").GetString()!;
            using var key = PassphraseWrap.Unwrap(wrap, passphrase);
            Assert.Equal(c.GetProperty("vk").GetString(), Convert.ToHexStringLower(key.Export()));
            using var viaNfd = PassphraseWrap.Unwrap(wrap, passphrase.Normalize(NormalizationForm.FormD));
            Assert.Equal(key.KeyId, viaNfd.KeyId);
        }
    }
}
