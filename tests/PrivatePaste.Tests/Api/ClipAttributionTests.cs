using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Clips;

namespace PrivatePaste.Tests.Api;

public class ClipAttributionTests
{
    private static async Task<ClipDto> PostAsync(HttpClient c, string text)
    {
        var r = await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, false), ProtocolJson.Options);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    private static async Task<ClipDto> GetAsync(HttpClient c, Guid id) =>
        (await c.GetFromJsonAsync<ClipDto>($"/api/clips/{id}", ProtocolJson.Options))!;

    [Fact]
    public async Task SinglePerson_SourceUserNameNull()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        var clip = await PostAsync(owner, "hello");

        Assert.Null(clip.SourceUserName);
        Assert.Null((await GetAsync(owner, clip.Id)).SourceUserName);
    }

    [Fact]
    public async Task TwoPeople_ClipShowsWhoCopiedIt()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");

        var clip = await PostAsync(ana, "from ana");

        Assert.Equal("Ana", clip.SourceUserName);
        Assert.Equal("Ana", (await GetAsync(owner, clip.Id)).SourceUserName);
    }

    [Fact]
    public async Task Recopy_ByOtherPerson_UpdatesName()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");

        var first = await PostAsync(owner, "shared text");
        Assert.Equal("owner", first.SourceUserName);
        var again = await PostAsync(ana, "shared text");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Ana", again.SourceUserName);
    }

    [Fact]
    public async Task WebCopy_RecordsWebPerson()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");
        var clip = await PostAsync(ana, "restore me");

        var web = await f.LoginWebAsync();
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/");
        using var restore = new HttpRequestMessage(HttpMethod.Post, $"/api/clips/{clip.Id}/restore");
        restore.Headers.Add("RequestVerificationToken", token);
        var response = await web.SendAsync(restore);
        response.EnsureSuccessStatusCode();
        var restored = (await response.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;

        Assert.Equal("owner", restored.SourceUserName);
        Assert.Equal("web", restored.SourceDevicePlatform);
    }

    [Fact]
    public async Task Resequence_NewSeqs_LastCopiedAtUnchanged_NotLive()
    {
        using var f = new PrivatePasteFactory();
        var rec = new PinEndpointsTests.RecordingBroadcaster();
        var (app, owner) = await PinEndpointsTests.RecordedClientAsync(f, rec);
        using var _ = app;
        var a = await PostAsync(owner, "one");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = await PostAsync(owner, "two");
        var before = (await owner.GetFromJsonAsync<SyncResponse>("/api/sync?afterSeq=0", ProtocolJson.Options))!;
        rec.Changed.Clear();

        await app.Services.GetRequiredService<ClipService>().ResendAsync(null);

        var after = (await owner.GetFromJsonAsync<SyncResponse>($"/api/sync?afterSeq={before.LatestSeq}",
            ProtocolJson.Options))!;
        Assert.Equal([a.Id, b.Id], after.Clips.Select(c => c.Id));
        Assert.All(after.Clips, c => Assert.True(c.Seq > before.LatestSeq));
        Assert.Equal(a.LastCopiedAt, after.Clips[0].LastCopiedAt);
        Assert.Equal(b.LastCopiedAt, after.Clips[1].LastCopiedAt);

        Assert.Equal([a.Id, b.Id], rec.Changed.Select(c => c.Clip.Id));
        Assert.All(rec.Changed, c => Assert.False(c.Live));
    }
}
