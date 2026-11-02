using System.Security.Cryptography;
using PrivatePaste.Client.Core.Files;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;

namespace PrivatePaste.Tests.Client;

public sealed class ClipFileStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-files-" + Guid.NewGuid().ToString("N"));
    private readonly VaultKey _key = VaultKey.Create();
    private ClipCodec Codec => new(_key);

    private string Root => Path.Combine(_dir, "files");

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<byte[]> SealAsync(byte[] plain)
    {
        using var output = new MemoryStream();
        await BlobCipher.EncryptAsync(_key, new MemoryStream(plain), output, default);
        return output.ToArray();
    }

    /// <summary>A decoded file clip with these names and contents, and a fetch that serves its sealed blobs.</summary>
    private async Task<(ClipDto Clip, Func<int, CancellationToken, Task<byte[]>> Fetch, List<int> Fetched)> ClipAsync(
        params (string Name, byte[] Content)[] files)
    {
        var blobs = new List<byte[]>();
        foreach (var f in files) blobs.Add(await SealAsync(f.Content));
        var clip = new ClipDto(Guid.NewGuid(), 1, ClipKind.File, null, "h", null, null, files.Sum(f => (long)f.Content.Length),
            Guid.NewGuid(), "B", T0)
        {
            BlobCount = files.Length,
            Files = files.Select(f => new ClipFileInfo(f.Name, f.Content.Length, "application/octet-stream")).ToList(),
        };
        var fetched = new List<int>();
        return (clip, (n, _) => { fetched.Add(n); return Task.FromResult(blobs[n]); }, fetched);
    }

    [Fact]
    public async Task Materialize_writes_files_in_payload_order()
    {
        var store = new ClipFileStore(Root);
        var (clip, fetch, fetched) = await ClipAsync(("b.txt", "bee"u8.ToArray()), ("a.txt", "ay"u8.ToArray()), ("empty", []));

        var paths = await store.MaterializeAsync(clip, fetch, Codec, default);

        var folder = Path.Combine(Path.GetFullPath(Root), clip.Id.ToString("N"));
        Assert.Equal([Path.Combine(folder, "b.txt"), Path.Combine(folder, "a.txt"), Path.Combine(folder, "empty")], paths);
        Assert.Equal("bee"u8.ToArray(), File.ReadAllBytes(paths[0]));
        Assert.Equal("ay"u8.ToArray(), File.ReadAllBytes(paths[1]));
        Assert.Empty(File.ReadAllBytes(paths[2]));
        Assert.All(paths, p => Assert.True(store.IsInside(p)));

        // A second apply of the same clip reuses the complete files: nothing is fetched again.
        fetched.Clear();
        Assert.Equal(paths, await store.MaterializeAsync(clip, fetch, Codec, default));
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Materialize_keeps_hostile_names_inside_the_clip_folder()
    {
        var store = new ClipFileStore(Root);
        string[] hostile = ["../../evil.exe", "C:\\x\\a.txt", "CON.txt", "a<b>.txt", "..", "", "dir/", "a\tb\u007F.txt"];
        var (clip, fetch, _) = await ClipAsync(hostile.Select(n => (n, "x"u8.ToArray())).ToArray());

        var paths = await store.MaterializeAsync(clip, fetch, Codec, default);

        var folder = Path.Combine(Path.GetFullPath(Root), clip.Id.ToString("N"));
        Assert.Equal(hostile.Length, paths.Count);
        foreach (var p in paths)
        {
            Assert.Equal(folder, Path.GetDirectoryName(Path.GetFullPath(p)));
            Assert.True(File.Exists(p));
        }
        Assert.Equal(["evil.exe", "a.txt", "_CON.txt", "ab.txt", "file", "file (2)", "file (3)", "ab (2).txt"],
            paths.Select(Path.GetFileName));
        Assert.Equal(hostile.Length, Directory.GetFiles(folder).Length);
        Assert.Single(Directory.GetDirectories(Path.GetFullPath(Root)));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.exe")));
        Assert.False(store.IsInside(Path.Combine(_dir, "evil.exe")));
        Assert.False(store.IsInside(Path.Combine(Root, "..", "evil.exe")));
        Assert.False(store.IsInside(Root));
    }

    [Fact]
    public async Task Duplicate_names_get_numbered()
    {
        var store = new ClipFileStore(Root);
        var (clip, fetch, _) = await ClipAsync(("report.pdf", [1]), ("sub/report.pdf", [2]), ("Report.pdf", [3]), ("report (2).pdf", [4]),
            ("x.part", [5]), ("x", [6]));

        var names = (await store.MaterializeAsync(clip, fetch, Codec, default)).Select(Path.GetFileName).ToList();

        Assert.Equal(["report.pdf", "report (2).pdf", "Report (3).pdf", "report (2) (2).pdf", "x.part", "x (2)"], names);
    }

    [Fact]
    public async Task Tampered_blob_leaves_no_file()
    {
        var store = new ClipFileStore(Root);
        var big = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 5);   // several chunks: earlier ones open before the tamper
        var (clip, fetch, _) = await ClipAsync(("ok.txt", "fine"u8.ToArray()), ("big.bin", big));
        var good = await fetch(1, default);
        var tampered = (byte[])good.Clone();
        tampered[^3] ^= 0x55;   // in the final chunk
        Task<byte[]> Bad(int n, CancellationToken ct) => n == 1 ? Task.FromResult(tampered) : fetch(n, ct);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.MaterializeAsync(clip, Bad, Codec, default));

        var folder = Path.Combine(Path.GetFullPath(Root), clip.Id.ToString("N"));
        Assert.False(File.Exists(Path.Combine(folder, "big.bin")));
        Assert.False(File.Exists(Path.Combine(folder, "big.bin.part")));
        Assert.Empty(Directory.GetFiles(folder, "*.part"));

        var paths = await store.MaterializeAsync(clip, fetch, Codec, default);
        Assert.Equal(big, File.ReadAllBytes(paths[1]));
        Assert.Empty(Directory.GetFiles(folder, "*.part"));
    }

    [Fact]
    public async Task Concurrent_materializations_of_one_clip_both_succeed()
    {
        var store = new ClipFileStore(Root);
        var big = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        var (clip, fetch, _) = await ClipAsync(("big.bin", big));
        async Task<byte[]> Slow(int n, CancellationToken ct) { await Task.Delay(50, ct); return await fetch(n, ct); }

        var both = await Task.WhenAll(store.MaterializeAsync(clip, Slow, Codec, default), store.MaterializeAsync(clip, Slow, Codec, default));

        Assert.Equal(both[0], both[1]);
        Assert.Equal(big, File.ReadAllBytes(both[0][0]));
    }

    [Fact]
    public async Task A_write_failure_names_no_file()
    {
        var store = new ClipFileStore(Root);
        var (clip, fetch, _) = await ClipAsync(("secret-plan.txt", "x"u8.ToArray()));
        Directory.CreateDirectory(Path.Combine(Root, clip.Id.ToString("N"), "secret-plan.txt"));

        var e = await Assert.ThrowsAsync<ClipFileWriteException>(() => store.MaterializeAsync(clip, fetch, Codec, default));

        Assert.DoesNotContain("secret", e.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public async Task Prune_removes_folders_not_kept_then_oldest_over_the_cap()
    {
        var store = new ClipFileStore(Root, maxBytes: 250);
        var (gone, f1, _) = await ClipAsync(("a", new byte[100]));
        var (oldest, f2, _) = await ClipAsync(("b", new byte[100]));
        var (middle, f3, _) = await ClipAsync(("c", new byte[100]));
        var (newest, f4, _) = await ClipAsync(("d", new byte[100]));
        await store.MaterializeAsync(gone, f1, Codec, default);
        await store.MaterializeAsync(oldest, f2, Codec, default);
        await store.MaterializeAsync(middle, f3, Codec, default);
        await store.MaterializeAsync(newest, f4, Codec, default);
        string Folder(ClipDto c) => Path.Combine(Path.GetFullPath(Root), c.Id.ToString("N"));
        Directory.SetLastWriteTimeUtc(Folder(oldest), DateTime.UtcNow.AddHours(-3));
        Directory.SetLastWriteTimeUtc(Folder(middle), DateTime.UtcNow.AddHours(-2));
        Directory.SetLastWriteTimeUtc(Folder(newest), DateTime.UtcNow.AddHours(-1));

        store.Prune(new HashSet<Guid> { oldest.Id, middle.Id, newest.Id });

        Assert.False(Directory.Exists(Folder(gone)));     // not kept
        Assert.False(Directory.Exists(Folder(oldest)));   // 300 B > 250 B: the oldest goes
        Assert.True(Directory.Exists(Folder(middle)));
        Assert.True(Directory.Exists(Folder(newest)));

        store.Clear();
        Assert.False(Directory.Exists(Root));
        store.Clear();   // nothing there: no throw
    }

    [Fact]
    public async Task Prune_keeps_a_folder_being_written()
    {
        var store = new ClipFileStore(Root);
        var (clip, fetch, _) = await ClipAsync(("a.txt", "ay"u8.ToArray()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<byte[]> Held(int n, CancellationToken ct) { entered.SetResult(); await release.Task; return await fetch(n, ct); }

        var writing = store.MaterializeAsync(clip, Held, Codec, default);
        await entered.Task;
        store.Prune(new HashSet<Guid>());   // e.g. a Save of a clip that isn't in the history
        release.SetResult();

        Assert.Equal("ay"u8.ToArray(), File.ReadAllBytes(Assert.Single(await writing)));
    }

    [Fact]
    public async Task Prune_keeps_held_clips_and_the_one_on_the_clipboard()
    {
        var store = new ClipFileStore(Root, maxBytes: 0);   // over the cap: in use or not, nothing in use goes
        var (held, f1, _) = await ClipAsync(("a", new byte[10]));
        var (onClipboard, f2, _) = await ClipAsync(("b", new byte[10]));
        var heldPaths = await store.MaterializeAsync(held, f1, Codec, default);
        var clipboardPaths = await store.MaterializeAsync(onClipboard, f2, Codec, default);

        store.MarkOnClipboard(onClipboard.Id);
        using (store.Hold(held.Id))
        {
            store.Prune(new HashSet<Guid>());   // neither is in the history (an older search hit, a pinned page)
            Assert.True(File.Exists(heldPaths[0]));
            Assert.True(File.Exists(clipboardPaths[0]));
        }

        store.Prune(new HashSet<Guid>());   // released: the held one goes; the clipboard's stays
        Assert.False(File.Exists(heldPaths[0]));
        Assert.True(File.Exists(clipboardPaths[0]));
    }
}
