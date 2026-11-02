using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Cache;
using PrivatePaste.Client.Core.Files;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;

namespace PrivatePaste.Tests.Client;

/// <summary>Copying files on the desktop: what is sent, what is refused and what is said.</summary>
public sealed class ClipboardCoordinatorFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-coord-files-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeClipboard _clip = new();
    private readonly ListNotifier _notes = new();
    private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private PrivatePasteFactory _server = null!;
    private SwitchHandler _handler = null!;
    private PrivatePasteApi _api = null!;
    private HistoryCache _cache = null!;
    private OutboundQueue _queue = null!;
    private ClipboardCoordinator _c = null!;
    private VaultKey _vaultKey = null!;
    private ClipCodec _codec = null!;

    public ClipboardCoordinatorFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _server?.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task StartAsync(Dictionary<string, string>? settings = null)
    {
        _server = new PrivatePasteFactory(settings);
        _vaultKey = await _server.VaultKeyAsync();
        _codec = new ClipCodec(_vaultKey);
        _handler = new SwitchHandler(_server.Server.CreateHandler()) { Key = _vaultKey };
        _api = new PrivatePasteApi(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") });
        _api.Token = (await _api.LoginAsync(PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password, "A", "windows")).Token;
        _cache = new HistoryCache(Path.Combine(_dir, "h.enc"), _key);
        _queue = new OutboundQueue(Path.Combine(_dir, "q.enc"), _key);
        _c = new ClipboardCoordinator(_clip, _api, () => _codec, _cache, _queue, new EchoGuard(_time, TimeSpan.FromSeconds(30)),
            _notes, _time, () => false, files: new ClipFileStore(Path.Combine(_dir, "store")));
    }

    private string MakeFile(string relative, byte[] content)
    {
        var path = Path.Combine(_dir, "src", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string MakeFolder(string relative)
    {
        var path = Path.Combine(_dir, "src", relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task CopyAsync(params string[] paths)
    {
        _clip.Current = new ClipboardContent.Files(paths);
        await _c.OnClipboardChangedAsync(default);
    }

    private async Task<List<ClipDto>> ServerClipsAsync()
    {
        var client = await _server.LoginAsync("Observer");
        var page = await client.GetFromJsonAsync<ClipPage>("/api/clips?limit=50", ProtocolJson.Options);
        return page!.Items.Select(_codec.Decode).ToList();
    }

    [Fact]
    public async Task Files_upload_as_one_clip()
    {
        await StartAsync();
        var a = MakeFile("a.txt", "hello"u8.ToArray());
        var b = MakeFile("b.pdf", [1, 2, 3]);

        await CopyAsync(a, b);

        var clip = Assert.Single(await ServerClipsAsync());
        Assert.Equal(ClipKind.File, clip.Kind);
        Assert.Equal(2, clip.BlobCount);
        Assert.Equal([new ClipFileInfo("a.txt", 5, "text/plain"), new ClipFileInfo("b.pdf", 3, "application/pdf")], clip.Files);
        Assert.Equal(8, clip.SizeBytes);
        Assert.Equal("hello"u8.ToArray(), await _codec.DecodeBlobAsync(await _api.GetBlobAsync(clip.Id, 0), default));
        Assert.Equal(clip.Id, Assert.Single(_cache.Items).Clip.Id);
        Assert.Empty(_notes.Messages);

        await CopyAsync(a, b);   // the same files again: already sent
        Assert.Single(_handler.Uploads);
    }

    [Fact]
    public async Task Folders_are_skipped()
    {
        await StartAsync();
        var folder = MakeFolder("photos");
        MakeFile(Path.Combine("photos", "inside.jpg"), [9]);
        var a = MakeFile("a.txt", "hello"u8.ToArray());

        await CopyAsync(folder, a);

        var clip = Assert.Single(await ServerClipsAsync());
        Assert.Equal([new ClipFileInfo("a.txt", 5, "text/plain")], clip.Files);
        Assert.Empty(_notes.Messages);
    }

    [Fact]
    public async Task Only_folders_notify_and_upload_nothing()
    {
        await StartAsync();
        var one = MakeFolder("one");
        var two = MakeFolder("two");

        await CopyAsync(one, two);
        await CopyAsync(one, two);   // read again (the OS can raise several changes): said once

        Assert.Empty(_handler.Uploads);
        Assert.Empty(await ServerClipsAsync());
        Assert.Equal(["Folders aren't synced."], _notes.Messages);
    }

    [Fact]
    public async Task Too_many_files_notify_and_upload_nothing()
    {
        await StartAsync();
        var paths = Enumerable.Range(0, 101).Select(i => MakeFile($"f{i}.txt", [(byte)i])).ToArray();

        await CopyAsync(paths);

        Assert.Empty(_handler.Uploads);
        Assert.Equal(["Too many files. Copy at most 100 at once."], _notes.Messages);
    }

    [Fact]
    public async Task Over_the_limit_notifies_and_uploads_nothing()
    {
        await StartAsync(new Dictionary<string, string> { ["PP_MAX_FILE_MB"] = "1" });
        var a = MakeFile("a.bin", new byte[600 * 1024]);
        var b = MakeFile("b.bin", new byte[600 * 1024]);

        await CopyAsync(a, b);
        await CopyAsync(a, b);   // the same selection again: said once

        Assert.Empty(_handler.Uploads);
        Assert.Equal(["Files over 1 MB aren't synced."], _notes.Messages);

        await CopyAsync(a);   // within the limit
        Assert.Single(_handler.Uploads);
    }

    [Fact]
    public async Task Server_without_file_kind_notifies()
    {
        await StartAsync();
        _handler.Respond = r => r.RequestUri!.AbsolutePath == "/api/server-info"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"protocol":2,"kinds":["text","link","image"],"limits":{"textKb":1024,"imageMb":20}}""",
                    Encoding.UTF8, "application/json"),
            }
            : null;
        var a = MakeFile("a.txt", "hello"u8.ToArray());

        await CopyAsync(a);

        Assert.Empty(_handler.Uploads);
        Assert.Equal(["Update your PrivatePaste server to sync files."], _notes.Messages);
        _clip.Current = new ClipboardContent.Text("text still syncs");
        await _c.OnClipboardChangedAsync(default);
        Assert.Single(_handler.Uploads);
    }

    [Fact]
    public async Task Offline_file_copy_notifies_and_is_not_queued()
    {
        await StartAsync();
        var a = MakeFile("a.txt", "hello"u8.ToArray());
        _handler.Offline = true;

        await CopyAsync(a);

        Assert.Equal(0, _queue.Count);
        Assert.Equal(["Couldn't send the files. Copy them again when you're online."], _notes.Messages);

        // Copying them again once online sends them.
        _handler.Offline = false;
        _time.Advance(ClipboardCoordinator.LimitsRetryDelay);
        await CopyAsync(a);
        Assert.Equal("a.txt", Assert.Single(Assert.Single(await ServerClipsAsync()).Files!).Name);
    }

    [Fact]
    public async Task Resume_remembers_the_files_on_the_clipboard_without_sending_them()
    {
        await StartAsync();
        var a = MakeFile("a.txt", "hello"u8.ToArray());
        _clip.Current = new ClipboardContent.Files([a]);

        await _c.OnResumedAsync(default);
        await _c.OnClipboardChangedAsync(default);

        Assert.Empty(_handler.Uploads);
    }

    [Fact]
    public void Files_are_never_queued()
    {
        var queue = new OutboundQueue(Path.Combine(_dir, "q2.enc"), _key);
        Assert.Throws<ArgumentException>(() => queue.Enqueue(new ClipboardContent.Files([Path.Combine(_dir, "x")]), _time.GetUtcNow()));
        Assert.Equal(0, queue.Count);
    }
}
