using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Api;

public class ClipApiTests
{
    private static Task<ClipPage> List(HttpClient c, string query = "") =>
        c.GetFromJsonAsync<ClipPage>("/api/clips" + query, ProtocolJson.Options)!;

    private static byte[] RandomImage(int length = 64)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task Text_upload_roundtrip_and_kind()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync("PC-test");
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var clip = await f.UploadTextAsync(c, key, "https://x.io");
        Assert.Equal(ClipKind.Link, clip.Kind);
        Assert.Equal("PC-test", clip.SourceDeviceName);
        Assert.NotNull(clip.SourceDeviceId);
        Assert.Equal("windows", clip.SourceDevicePlatform);
        var page = await List(c);
        Assert.Equal(clip.Id, page.Items[0].Id);
        var got = await c.GetFromJsonAsync<ClipDto>($"/api/clips/{clip.Id}", ProtocolJson.Options);
        Assert.Null(got!.Text);
        Assert.Equal("https://x.io", ClipPayload.Open(key, got.Payload!).Text);
    }

    [Fact]
    public async Task Upload_stores_and_returns_the_devices_platform()
    {
        using var f = new PrivatePasteFactory();
        var phone = await f.LoginAsync("Phone", "Android");
        using var key = await PrivatePasteFactory.SetUpVaultAsync(phone);
        var clip = await f.UploadTextAsync(phone, key, "from the phone");
        Assert.Equal("android", clip.SourceDevicePlatform);

        var (image, _, _) = await f.UploadImageAsync(phone, key, RandomImage());
        Assert.Equal("android", image.SourceDevicePlatform);

        var listed = (await List(phone)).Items.Single(i => i.Id == clip.Id);
        Assert.Equal("android", listed.SourceDevicePlatform);
        var raw = await phone.GetStringAsync($"/api/clips/{clip.Id}");
        Assert.Contains("\"sourceDeviceName\":\"Phone\",\"sourceDevicePlatform\":\"android\"", raw);

        var sync = await phone.GetFromJsonAsync<SyncResponse>("/api/sync?afterSeq=0", ProtocolJson.Options);
        Assert.Equal("android", sync!.Clips.Single(i => i.Id == clip.Id).SourceDevicePlatform);
    }

    [Fact]
    public async Task Dedup_bump_and_restore_take_the_callers_platform()
    {
        using var f = new PrivatePasteFactory();
        var pc = await f.LoginAsync("PC", "windows");
        var mac = await f.LoginAsync("Mac", "macos");
        var linux = await f.LoginAsync("Box", "linux");
        using var key = await PrivatePasteFactory.SetUpVaultAsync(pc);

        var first = await f.UploadTextAsync(pc, key, "same");
        var bumped = await f.UploadTextAsync(mac, key, "same");
        Assert.Equal(first.Id, bumped.Id);
        Assert.Equal("macos", bumped.SourceDevicePlatform);

        var restored = await (await linux.PostAsync($"/api/clips/{first.Id}/restore", null))
            .Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options);
        Assert.Equal("linux", restored!.SourceDevicePlatform);
        Assert.Equal("Box", restored.SourceDeviceName);
    }

    [Fact]
    public async Task Same_image_twice_dedups()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var bytes = RandomImage();
        var (a, _, _) = await f.UploadImageAsync(c, key, bytes);
        var (b, _, _) = await f.UploadImageAsync(c, key, bytes);
        Assert.Equal(a.Id, b.Id);
        Assert.True(b.Seq > a.Seq);
        Assert.Single((await List(c)).Items);
    }

    [Fact]
    public async Task Live_upload_ignores_client_time()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var now = f.Clock.GetUtcNow();
        var clip = await f.UploadTextAsync(c, key, "live", now.AddHours(-1), backfill: false);
        Assert.Equal(now, clip.LastCopiedAt);
    }

    [Fact]
    public async Task Backfill_keeps_client_time_clamped_to_now()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var now = f.Clock.GetUtcNow();
        var past = await f.UploadTextAsync(c, key, "past", now.AddHours(-1), backfill: true);
        Assert.Equal(now.AddHours(-1), past.LastCopiedAt);
        var future = await f.UploadTextAsync(c, key, "future", now.AddHours(1), backfill: true);
        Assert.Equal(now, future.LastCopiedAt);
    }

    [Fact]
    public async Task Restore_bumps_to_top()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var a = await f.UploadTextAsync(c, key, "a");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = await f.UploadTextAsync(c, key, "b");
        Assert.Equal([b.Id, a.Id], (await List(c)).Items.Select(i => i.Id));

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var r = await c.PostAsync($"/api/clips/{a.Id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal([a.Id, b.Id], (await List(c)).Items.Select(i => i.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/api/clips/{Guid.NewGuid()}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_clip_and_blobs()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var (clip, _, _) = await f.UploadImageAsync(c, key, RandomImage());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(f.DataDir, "blobs")).Length);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/clips/{clip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}/blobs/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/clips/{clip.Id}")).StatusCode);
        var sync = (await c.GetFromJsonAsync<SyncResponse>("/api/sync?afterSeq=0", ProtocolJson.Options))!;
        Assert.Contains(clip.Id, sync.DeletedIds);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.DataDir, "blobs")));
    }

    [Fact]
    public async Task Delete_succeeds_even_if_a_blob_file_cannot_be_removed()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var (clip, _, _) = await f.UploadImageAsync(c, key, RandomImage());
        var blob = Directory.GetFiles(Path.Combine(f.DataDir, "blobs"))[0];
        // On Windows an open handle without FileShare.Delete makes File.Delete throw an IOException.
        using (new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/clips/{clip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_cursor_400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/clips?cursor=!!!")).StatusCode);
    }

    [Fact]
    public async Task Reupload_restores_missing_blobs()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var bytes = RandomImage();
        var (clip, _, _) = await f.UploadImageAsync(c, key, bytes);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/clips/{clip.Id}/blobs/0")).StatusCode);

        foreach (var file in Directory.GetFiles(Path.Combine(f.DataDir, "blobs")))
            File.Delete(file);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}/blobs/0")).StatusCode);

        var (again, _, _) = await f.UploadImageAsync(c, key, bytes);
        Assert.Equal(clip.Id, again.Id);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/clips/{clip.Id}/blobs/0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/clips/{clip.Id}/thumb")).StatusCode);
    }

    [Fact]
    public async Task Reupload_repairs_corrupt_blobs()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var (meta, blob, thumb) = await PrivatePasteFactory.SealImageAsync(key, RandomImage());
        var first = await PrivatePasteFactory.PostClipAsync(c, meta, thumb, blob);
        first.EnsureSuccessStatusCode();
        var clip = (await first.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;

        // As if a crash had left half-written blob files behind.
        foreach (var file in Directory.GetFiles(Path.Combine(f.DataDir, "blobs")))
            File.WriteAllBytes(file, [1, 2, 3]);

        (await PrivatePasteFactory.PostClipAsync(c, meta, thumb, blob)).EnsureSuccessStatusCode();
        Assert.Equal(blob, await c.GetByteArrayAsync($"/api/clips/{clip.Id}/blobs/0"));
        Assert.Equal(thumb, await c.GetByteArrayAsync($"/api/clips/{clip.Id}/thumb"));
        Assert.All(Directory.GetFiles(Path.Combine(f.DataDir, "blobs")), p => Assert.EndsWith(".enc", p));
    }

    [Fact]
    public async Task Meta_after_the_blob_parts_is_honored()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var now = f.Clock.GetUtcNow();
        var (meta, blob, thumb) = await PrivatePasteFactory.SealImageAsync(key, RandomImage(), now.AddHours(-1), backfill: true);
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(blob), "blob0", "blob0" },
            { new ByteArrayContent(thumb), "thumb", "thumb" },
            { new StringContent(JsonSerializer.Serialize(meta, ProtocolJson.Options), Encoding.UTF8, "application/json"), "meta" },
        };
        var r = await c.PostAsync("/api/clips", form);
        r.EnsureSuccessStatusCode();
        var clip = (await r.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
        Assert.Equal(now.AddHours(-1), clip.LastCopiedAt);
        Assert.True(clip.HasThumb);
    }

    [Fact]
    public async Task Malformed_multipart_body_400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes("--abc\r\nthis is not a header\r\n\r\nx\r\n--abc--\r\n"));
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=abc");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/clips", content)).StatusCode);
    }

    [Fact]
    public async Task Upload_that_is_not_multipart_400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var r = await c.PostAsJsonAsync("/api/clips", PrivatePasteFactory.TextMeta(key, "json body"), ProtocolJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
