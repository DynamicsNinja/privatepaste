using System.Net;
using System.Net.Http.Json;
using PrivatePaste.Protocol;
using PrivatePaste.Tests.Images;

namespace PrivatePaste.Tests.Api;

public class ClipApiTests
{
    private static async Task<ClipDto> PostText(HttpClient c, string text, DateTimeOffset? at = null, bool backfill = false)
    {
        var r = await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, at, backfill), ProtocolJson.Options);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    private static Task<HttpResponseMessage> PostImage(HttpClient c, byte[] bytes, DateTimeOffset? copiedAt = null, bool backfill = false)
    {
        var form = new MultipartFormDataContent();
        if (copiedAt is not null) form.Add(new StringContent(copiedAt.Value.ToString("O")), "copiedAt");
        if (backfill) form.Add(new StringContent("true"), "backfill");
        form.Add(new ByteArrayContent(bytes), "file", "x.png");
        return c.PostAsync("/api/clips/image", form);
    }

    private static async Task<ClipDto> PostImageOk(HttpClient c, byte[] bytes, DateTimeOffset? copiedAt = null, bool backfill = false)
    {
        var r = await PostImage(c, bytes, copiedAt, backfill);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    private static Task<ClipPage> List(HttpClient c, string query = "") =>
        c.GetFromJsonAsync<ClipPage>("/api/clips" + query, ProtocolJson.Options)!;

    [Fact]
    public async Task Text_upload_roundtrip_and_kind()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync("PC-test");
        var clip = await PostText(c, "https://x.io");
        Assert.Equal(ClipKind.Link, clip.Kind);
        Assert.Equal("PC-test", clip.SourceDeviceName);
        Assert.NotNull(clip.SourceDeviceId);
        var page = await List(c);
        Assert.Equal(clip.Id, page.Items[0].Id);
        var got = await c.GetFromJsonAsync<ClipDto>($"/api/clips/{clip.Id}", ProtocolJson.Options);
        Assert.Equal("https://x.io", got!.Text);
    }

    [Fact]
    public async Task Crlf_text_stored_normalized()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var a = await PostText(c, "a\r\nb");
        Assert.Equal("a\nb", a.Text);
        var b = await PostText(c, "a\nb");
        Assert.Equal(a.Id, b.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    public async Task Empty_text_400(string text)
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var r = await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, false), ProtocolJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Text_over_limit_413()
    {
        using var f = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_TEXT_KB"] = "1" });
        var c = await f.LoginAsync();
        var over = await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(new string('a', 1025), null, false), ProtocolJson.Options);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        var ok = await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(new string('a', 1024), null, false), ProtocolJson.Options);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Image_upload_returns_dims_and_serves_png_and_thumbnail()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var clip = await PostImageOk(c, TestImages.MakePng(600, 300).ToArray());
        Assert.Equal(ClipKind.Image, clip.Kind);
        Assert.Equal(600, clip.Width);
        Assert.Equal(300, clip.Height);

        var full = await c.GetAsync($"/api/clips/{clip.Id}/image");
        Assert.Equal("image/png", full.Content.Headers.ContentType?.MediaType);
        Assert.Equal((600, 300), TestImages.Dims(await full.Content.ReadAsByteArrayAsync()));
        var thumb = await c.GetAsync($"/api/clips/{clip.Id}/thumbnail");
        Assert.Equal((256, 128), TestImages.Dims(await thumb.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Image_endpoints_404_for_text_clip_and_unknown_clip()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var clip = await PostText(c, "hello");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}/image")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Same_image_twice_dedups()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var bytes = TestImages.MakePng(20, 20).ToArray();
        var a = await PostImageOk(c, bytes);
        var b = await PostImageOk(c, bytes);
        Assert.Equal(a.Id, b.Id);
        Assert.True(b.Seq > a.Seq);
        Assert.Single((await List(c)).Items);
    }

    [Fact]
    public async Task Garbage_image_400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostImage(c, [1, 2, 3, 4, 5])).StatusCode);
    }

    [Fact]
    public async Task Image_over_limit_413()
    {
        using var f = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_IMAGE_MB"] = "1" });
        var c = await f.LoginAsync();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostImage(c, new byte[1024 * 1024 + 1])).StatusCode);
    }

    [Fact]
    public async Task Live_upload_ignores_client_time()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var now = f.Clock.GetUtcNow();
        var clip = await PostText(c, "live", now.AddHours(-1), backfill: false);
        Assert.Equal(now, clip.LastCopiedAt);
    }

    [Fact]
    public async Task Backfill_keeps_client_time_clamped_to_now()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var now = f.Clock.GetUtcNow();
        var past = await PostText(c, "past", now.AddHours(-1), backfill: true);
        Assert.Equal(now.AddHours(-1), past.LastCopiedAt);
        var future = await PostText(c, "future", now.AddHours(1), backfill: true);
        Assert.Equal(now, future.LastCopiedAt);
    }

    [Fact]
    public async Task Restore_bumps_to_top()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var a = await PostText(c, "a");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = await PostText(c, "b");
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
        var clip = await PostImageOk(c, TestImages.MakePng(10, 10).ToArray());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/clips/{clip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}/image")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/clips/{clip.Id}")).StatusCode);
        var sync = (await c.GetFromJsonAsync<SyncResponse>("/api/sync?afterSeq=0", ProtocolJson.Options))!;
        Assert.Contains(clip.Id, sync.DeletedIds);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.DataDir, "blobs")));
    }

    [Fact]
    public async Task Search_query_param_works()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        await PostText(c, "docker compose up");
        await PostText(c, "something else");
        var page = await List(c, "?q=compose");
        Assert.Single(page.Items);
        Assert.Equal("docker compose up", page.Items[0].Text);
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
        var bytes = TestImages.MakePng(30, 30).ToArray();
        var clip = await PostImageOk(c, bytes);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/clips/{clip.Id}/image")).StatusCode);

        foreach (var file in Directory.GetFiles(Path.Combine(f.DataDir, "blobs")))
            File.Delete(file);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}/image")).StatusCode);

        var again = await PostImageOk(c, bytes);
        Assert.Equal(clip.Id, again.Id);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/clips/{clip.Id}/image")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/clips/{clip.Id}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task Image_upload_does_not_buffer_to_temp_files()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var bytes = TestImages.MakePng(200, 200).ToArray();
        Assert.True(bytes.Length > 100 * 1024);
        // Buffered form files are deleted when the request ends, so watch for their creation instead.
        var created = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var watcher = new FileSystemWatcher(Path.GetTempPath(), "ASPNETCORE_*") { EnableRaisingEvents = true };
        watcher.Created += (_, e) => created.Add(e.FullPath);
        await PostImageOk(c, bytes);
        await Task.Delay(300);
        Assert.Empty(created);
    }

    [Fact]
    public async Task Image_without_file_part_400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var form = new MultipartFormDataContent { { new StringContent("true"), "backfill" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/clips/image", form)).StatusCode);
    }
}
