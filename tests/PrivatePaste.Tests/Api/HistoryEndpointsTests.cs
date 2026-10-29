using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PrivatePaste.Protocol;
using PrivatePaste.Tests.Images;

namespace PrivatePaste.Tests.Api;

public class HistoryEndpointsTests
{
    private static Task<HttpResponseMessage> Backfill(HttpClient c, string text, DateTimeOffset at) =>
        c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, at, true), ProtocolJson.Options);

    private static Task<SettingsDto?> GetSettings(HttpClient c) =>
        c.GetFromJsonAsync<SettingsDto>("/api/settings", ProtocolJson.Options);

    [Fact]
    public async Task Clear_deletes_unpinned_keeps_pins_and_reports_the_count()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var keep = await PinEndpointsTests.PostText(c, "keep");
        await PinEndpointsTests.PostText(c, "drop one");
        await PinEndpointsTests.PostText(c, "drop two");
        await c.PostAsync($"/api/clips/{keep.Id}/pin", null);

        var r = await c.PostAsync("/api/history/clear", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("deleted").GetInt32());

        var page = (await c.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options))!;
        Assert.Equal(keep.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Clear_removes_image_blobs_and_broadcasts_each_delete()
    {
        using var f = new PrivatePasteFactory();
        var rec = new PinEndpointsTests.RecordingBroadcaster();
        var (app, client) = await PinEndpointsTests.RecordedClientAsync(f, rec);
        using var _ = app;

        var text = await PinEndpointsTests.PostText(client, "t");
        var form = new MultipartFormDataContent { { new ByteArrayContent(TestImages.MakePng(4, 4).ToArray()), "file", "x.png" } };
        var img = (await (await client.PostAsync("/api/clips/image", form)).Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clips/{img.Id}/image")).StatusCode);

        await client.PostAsync("/api/history/clear", null);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/clips/{img.Id}/image")).StatusCode);
        Assert.Equivalent(new[] { text.Id, img.Id }, rec.Deleted);
    }

    [Fact]
    public async Task Backfill_copied_before_a_clear_is_gone_but_live_uploads_still_work()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var before = f.Clock.GetUtcNow().AddMinutes(-5);
        await PinEndpointsTests.PostText(c, "x");
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await c.PostAsync("/api/history/clear", null);

        var r = await Backfill(c, "old", before);
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode);
        using var problem = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("Cleared", problem.RootElement.GetProperty("title").GetString());

        var form = new MultipartFormDataContent
        {
            { new StringContent(before.ToString("O")), "copiedAt" },
            { new StringContent("true"), "backfill" },
            { new ByteArrayContent(TestImages.MakePng(4, 4).ToArray()), "file", "x.png" },
        };
        Assert.Equal(HttpStatusCode.Gone, (await c.PostAsync("/api/clips/image", form)).StatusCode);

        Assert.Empty((await c.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options))!.Items);

        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await Backfill(c, "after", f.Clock.GetUtcNow().AddMinutes(-1))).StatusCode);
        await PinEndpointsTests.PostText(c, "live");
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

        Assert.Equal(HttpStatusCode.Gone, (await Backfill(c, "ancient", now.AddDays(-8))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Backfill(c, "recent", now.AddDays(-6))).StatusCode);
    }
}
