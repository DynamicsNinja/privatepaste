using System.Text;
using PrivatePaste.Client.Core.Security;

namespace PrivatePaste.Tests.Client;

public class SecretFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-secret-" + Guid.NewGuid().ToString("N"));
    private string Path_ => Path.Combine(_dir, "token.bin");
    private readonly FakeProtector _fake = new();

    public SecretFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // XORs with a constant and prefixes a header so foreign data fails to unprotect.
    private sealed class FakeProtector : ISecretProtector
    {
        private static readonly byte[] Header = "FAKE"u8.ToArray();
        public byte[] Protect(byte[] plaintext) => [.. Header, .. plaintext.Select(b => (byte)(b ^ 0x5A))];
        public byte[] Unprotect(byte[] protectedData)
        {
            if (protectedData.Length < Header.Length || !protectedData.AsSpan(0, Header.Length).SequenceEqual(Header))
                throw new System.Security.Cryptography.CryptographicException("bad header");
            return protectedData.Skip(Header.Length).Select(b => (byte)(b ^ 0x5A)).ToArray();
        }
    }

    [Fact]
    public void Roundtrip()
    {
        var f = new SecretFile(Path_, _fake);
        f.Write("tok-123");
        Assert.Equal("tok-123", f.Read());
    }

    [Fact]
    public void File_is_not_plaintext()
    {
        new SecretFile(Path_, _fake).Write("tok-123");
        Assert.DoesNotContain("tok-123", File.ReadAllText(Path_));
    }

    [Fact]
    public void Missing_returns_null() =>
        Assert.Null(new SecretFile(Path.Combine(_dir, "nope.bin"), _fake).Read());

    [Fact]
    public void Garbage_returns_null()
    {
        File.WriteAllBytes(Path_, [1, 2, 3]);
        Assert.Null(new SecretFile(Path_, _fake).Read());
    }

    [Fact]
    public void Write_creates_directory_and_overwrites()
    {
        var f = new SecretFile(Path.Combine(_dir, "sub", "x.bin"), _fake);
        f.Write("a");
        f.Write("b");
        Assert.Equal("b", f.Read());
    }

    [Fact]
    public void Delete_removes_file_and_tolerates_missing()
    {
        var f = new SecretFile(Path_, _fake);
        f.Write("a");
        f.Delete();
        Assert.Null(f.Read());
        f.Delete();
    }

    [Fact, Trait("os", "windows")]
    public void Dpapi_roundtrip()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = new DpapiSecretProtector();
        var plain = Encoding.UTF8.GetBytes("tok-123");
        var enc = p.Protect(plain);
        Assert.DoesNotContain("tok-123", Encoding.UTF8.GetString(enc));
        Assert.Equal(plain, p.Unprotect(enc));
        var f = new SecretFile(Path_, p);
        f.Write("tok-123");
        Assert.Equal("tok-123", f.Read());
    }
}
