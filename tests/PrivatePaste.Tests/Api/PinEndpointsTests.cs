using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Clips;

namespace PrivatePaste.Tests.Api;

public class PinEndpointsTests
{
    internal sealed class RecordingBroadcaster : IClipBroadcaster
    {
        public List<(ClipDto Clip, bool Live)> Changed { get; } = [];
        public List<Guid> Deleted { get; } = [];

        public Task ClipChangedAsync(ClipDto clip, bool live)
        {
            lock (Changed) Changed.Add((clip, live));
            return Task.CompletedTask;
        }

        public Task ClipDeletedAsync(Guid id, long seq)
        {
            lock (Deleted) Deleted.Add(id);
            return Task.CompletedTask;
        }
    }

    /// <summary>A client of a host whose broadcasts are recorded instead of sent to WebSockets.</summary>
    internal static async Task<(WebApplicationFactory<Program> App, HttpClient Client)> RecordedClientAsync(
        PrivatePasteFactory f, RecordingBroadcaster rec)
    {
        var app = f.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IClipBroadcaster>();
            s.AddSingleton<IClipBroadcaster>(rec);
        }));
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(PrivatePasteFactory.Password, "Recorded", "windows") { Username = PrivatePasteFactory.OwnerName }, ProtocolJson.Options);
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            (await login.Content.ReadFromJsonAsync<LoginResponse>(ProtocolJson.Options))!.Token);
        return (app, client);
    }

    internal static async Task<ClipDto> PostText(HttpClient c, string text)
    {
        var r = await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, false), ProtocolJson.Options);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    [Fact]
    public async Task Pin_and_unpin_return_the_clip_and_404_when_missing()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var clip = await PostText(c, "keep me");

        var pin = await c.PostAsync($"/api/clips/{clip.Id}/pin", null);
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        var pinned = (await pin.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
        Assert.Equal(f.Clock.GetUtcNow(), pinned.PinnedAt);

        var unpin = await c.DeleteAsync($"/api/clips/{clip.Id}/pin");
        Assert.Equal(HttpStatusCode.OK, unpin.StatusCode);
        Assert.Null((await unpin.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!.PinnedAt);

        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/api/clips/{Guid.NewGuid()}/pin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/clips/{Guid.NewGuid()}/pin")).StatusCode);
    }

    [Fact]
    public async Task Pin_broadcasts_not_live_only_when_it_changed()
    {
        using var f = new PrivatePasteFactory();
        var rec = new RecordingBroadcaster();
        var (app, client) = await RecordedClientAsync(f, rec);
        using var _ = app;

        var clip = await PostText(client, "a");
        rec.Changed.Clear();

        await client.PostAsync($"/api/clips/{clip.Id}/pin", null);
        var change = Assert.Single(rec.Changed);
        Assert.False(change.Live);
        Assert.NotNull(change.Clip.PinnedAt);

        await client.PostAsync($"/api/clips/{clip.Id}/pin", null);
        Assert.Single(rec.Changed);

        await client.DeleteAsync($"/api/clips/{clip.Id}/pin");
        Assert.Equal(2, rec.Changed.Count);
        await client.DeleteAsync($"/api/clips/{clip.Id}/pin");
        Assert.Equal(2, rec.Changed.Count);
    }

    [Fact]
    public async Task Pinned_list_is_filtered_and_pin_keeps_position()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        var a = await PostText(c, "alpha");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = await PostText(c, "bravo");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var d = await PostText(c, "delta");

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await c.PostAsync($"/api/clips/{a.Id}/pin", null);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await c.PostAsync($"/api/clips/{d.Id}/pin", null);

        var all = (await c.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options))!;
        Assert.Equal([d.Id, b.Id, a.Id], all.Items.Select(i => i.Id));
        Assert.Equal(a.LastCopiedAt, all.Items.Single(i => i.Id == a.Id).LastCopiedAt);

        var pinned = (await c.GetFromJsonAsync<ClipPage>("/api/clips?pinned=true", ProtocolJson.Options))!;
        Assert.Equal(2, pinned.Items.Count);
        Assert.DoesNotContain(pinned.Items, i => i.Id == b.Id);

        var searched = (await c.GetFromJsonAsync<ClipPage>("/api/clips?pinned=true&q=alp", ProtocolJson.Options))!;
        Assert.Equal(a.Id, Assert.Single(searched.Items).Id);
    }
}
