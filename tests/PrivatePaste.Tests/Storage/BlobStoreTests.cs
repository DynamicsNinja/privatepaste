using System.Security.Cryptography;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Storage;

public sealed class BlobStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pp-blob-" + Guid.NewGuid().ToString("N"));
    private readonly BlobStore store;

    public BlobStoreTests() =>
        store = new BlobStore(new ServerOptions("0123456789abcdef-key", "pw", dir, 1000, 1000));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private string DiskPath(string name) => Path.Combine(dir, "blobs", name + ".enc");

    [Fact]
    public async Task Roundtrip()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await store.WriteAsync("a.png", bytes);
        Assert.Equal(bytes, await store.ReadAsync("a.png"));
    }

    [Fact]
    public async Task On_disk_bytes_are_not_plaintext()
    {
        var canary = "CANARY-PLAINTEXT-CANARY-PLAINTEXT"u8.ToArray();
        await store.WriteAsync("c.png", canary);
        var raw = await File.ReadAllBytesAsync(DiskPath("c.png"));
        Assert.Equal(12 + 16 + canary.Length, raw.Length);
        Assert.True(raw.AsSpan().IndexOf(canary) < 0);
    }

    [Fact]
    public async Task Missing_returns_null() => Assert.Null(await store.ReadAsync("nope"));

    [Fact]
    public async Task Tampered_file_throws()
    {
        await store.WriteAsync("t.png", new byte[] { 9, 8, 7, 6 });
        var path = DiskPath("t.png");
        var raw = await File.ReadAllBytesAsync(path);
        raw[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, raw);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAsync("t.png"));
    }

    [Fact]
    public async Task Delete_removes_and_missing_is_noop()
    {
        await store.WriteAsync("d.png", new byte[] { 1 });
        store.Delete("d.png");
        Assert.Null(await store.ReadAsync("d.png"));
        store.Delete("d.png");
    }

    [Fact]
    public async Task Same_plaintext_encrypts_differently()
    {
        var b = new byte[] { 1, 2, 3 };
        await store.WriteAsync("x", b);
        var first = await File.ReadAllBytesAsync(DiskPath("x"));
        await store.WriteAsync("x", b);
        Assert.NotEqual(first, await File.ReadAllBytesAsync(DiskPath("x")));
    }
}
