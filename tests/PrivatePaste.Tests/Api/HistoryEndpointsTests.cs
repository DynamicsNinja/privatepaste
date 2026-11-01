using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Api;

public class HistoryEndpointsTests
{
    private static async Task<HttpResponseMessage> Backfill(PrivatePasteFactory f, HttpClient c, string text, DateTimeOffset at) =>
        await PrivatePasteFactory.PostClipAsync(c, PrivatePasteFactory.TextMeta(await f.VaultKeyAsync(), text, at, backfill: true));

    private static Task<SettingsDto?> GetSettings(HttpClient c) =>
        c.GetFromJsonAsync<SettingsDto>("/api/settings", ProtocolJson.Options);

    [Fact]
    public async Task Clear_deletes_unpinned_keeps_pins_and_reports_the_count()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var keep = await f.UploadTextAsync(c, "keep");
        await f.UploadTextAsync(c, "drop one");
        await f.UploadTextAsync(c, "drop two");
        await c.PostAsync($"/api/clips/{keep.Id}/pin", null);
        f.Clock.Advance(TimeSpan.FromSeconds(1));   // a clear only deletes clips copied before it

        var r = await c.PostAsync("/api/history/clear", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("deleted").GetInt32());

        var page = (await c.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options))!;
        Assert.Equal(keep.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Clear_keeps_a_clip_copied_at_or_after_the_clear_time()
    {
        // A live upload that lands while the clear runs has a copy time at or after the clear: it must not vanish.
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        await f.UploadTextAsync(c, "before");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var racing = await f.UploadTextAsync(c, "racing");

        var r = await c.PostAsync("/api/history/clear", null);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("deleted").GetInt32());

        var page = (await c.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options))!;
        Assert.Equal(racing.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Clear_removes_image_blobs_and_broadcasts_each_delete()
    {
        using var f = new PrivatePasteFactory();
        var rec = new PinEndpointsTests.RecordingBroadcaster();
        var (app, client) = await PinEndpointsTests.RecordedClientAsync(f, rec);
        using var _ = app;

        var text = await f.UploadTextAsync(client, "t");
        var (img, _, _) = await f.UploadImageAsync(client, await f.VaultKeyAsync(), [1, 2, 3, 4]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clips/{img.Id}/blobs/0")).StatusCode);
        f.Clock.Advance(TimeSpan.FromSeconds(1));

        await client.PostAsync("/api/history/clear", null);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/clips/{img.Id}/blobs/0")).StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.DataDir, "blobs")));
        Assert.Equivalent(new[] { text.Id, img.Id }, rec.Deleted);
    }

    [Fact]
    public async Task Backfill_copied_before_a_clear_is_gone_but_live_uploads_still_work()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var before = f.Clock.GetUtcNow().AddMinutes(-5);
        await f.UploadTextAsync(c, "x");
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await c.PostAsync("/api/history/clear", null);

        var r = await Backfill(f, c, "old", before);
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode);
        using var problem = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("Cleared", problem.RootElement.GetProperty("title").GetString());
        // Android 0.1.2 shows the detail as its notification text.
        Assert.Equal("History was cleared after this clip was copied, so it wasn't added.",
            problem.RootElement.GetProperty("detail").GetString());

        var (meta, blob, thumb) = await PrivatePasteFactory.SealImageAsync(await f.VaultKeyAsync(), [1, 2, 3, 4], before, backfill: true);
        Assert.Equal(HttpStatusCode.Gone, (await PrivatePasteFactory.PostClipAsync(c, meta, thumb, blob)).StatusCode);

        Assert.Empty((await c.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options))!.Items);

        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await Backfill(f, c, "after", f.Clock.GetUtcNow().AddMinutes(-1))).StatusCode);
        await f.UploadTextAsync(c, "live");
    }

    [Fact]
    public async Task Settings_roundtrip_and_reject_other_values()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        Assert.Null((await GetSettings(c))!.AutoDeleteDays);

        var put = await c.PutAsJsonAsync("/api/settings", new SettingsDto(30), ProtocolJson.Options);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(30, (await GetSettings(c))!.AutoDeleteDays);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync("/api/settings", new SettingsDto(14), ProtocolJson.Options)).StatusCode);
        Assert.Equal(30, (await GetSettings(c))!.AutoDeleteDays);

        await c.PutAsJsonAsync("/api/settings", new SettingsDto(null), ProtocolJson.Options);
        Assert.Null((await GetSettings(c))!.AutoDeleteDays);
    }

    [Fact]
    public async Task Backfill_older_than_the_auto_delete_age_is_gone()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        await c.PutAsJsonAsync("/api/settings", new SettingsDto(7), ProtocolJson.Options);
        var now = f.Clock.GetUtcNow();

        Assert.Equal(HttpStatusCode.Gone, (await Backfill(f, c, "ancient", now.AddDays(-8))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Backfill(f, c, "recent", now.AddDays(-6))).StatusCode);
    }
}
