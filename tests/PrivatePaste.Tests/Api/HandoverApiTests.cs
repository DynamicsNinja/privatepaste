using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Api;

public class HandoverApiTests
{
    private static Task<HttpResponseMessage> PostAsync(HttpClient c, byte[] blob) =>
        c.PostAsJsonAsync("/api/vault/handovers", new HandoverCreateRequest(Convert.ToBase64String(blob)), ProtocolJson.Options);

    private static async Task<HandoverCreated> CreateAsync(HttpClient c, byte[] blob)
    {
        var response = await PostAsync(c, blob);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HandoverCreated>(ProtocolJson.Options))!;
    }

    [Fact]
    public async Task Handover_IsSingleUse()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        var blob = new byte[] { 1, 2, 3, 4 };
        var created = await CreateAsync(owner, blob);
        Assert.Equal(f.Clock.GetUtcNow().AddMinutes(10), created.ExpiresAt);

        var first = await owner.GetAsync($"/api/vault/handovers/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var got = await first.Content.ReadFromJsonAsync<HandoverBlob>(ProtocolJson.Options);
        Assert.Equal(blob, Convert.FromBase64String(got!.Blob));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/vault/handovers/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Handover_ExpiresAfter10Minutes()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        var created = await CreateAsync(owner, [1, 2, 3]);

        f.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/vault/handovers/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Handover_ConcurrentTake_OnlyOneWins()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        var created = await CreateAsync(owner, [9, 9, 9]);
        // The create above stamped the device's last-seen time, so the parallel takes below don't also race to
        // write it (a first-request-per-minute write that can hit "database is locked" and hide the real result).
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => owner.GetAsync($"/api/vault/handovers/{created.Id}"))));

        var statuses = string.Join(",", responses.Select(r => (int)r.StatusCode).Order());
        Assert.True(statuses == "200,404,404,404,404,404,404,404", statuses);
    }

    [Fact]
    public async Task Handover_TooLarge_Is413()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(owner, new byte[1024])).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostAsync(owner, new byte[1025])).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostAsync(owner, new byte[100_000])).StatusCode);
    }

    [Fact]
    public async Task Handover_RejectsEmptyAndNonCanonicalBlobs()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();

        foreach (var bad in new[] { "", "not base64!", "AQID\n", "AQJ=" })
        {
            var response = await owner.PostAsJsonAsync("/api/vault/handovers", new HandoverCreateRequest(bad), ProtocolJson.Options);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Handover_RequiresSignIn()
    {
        using var f = new PrivatePasteFactory();
        var anonymous = f.CreateClient();

        var post = await anonymous.PostAsJsonAsync("/api/vault/handovers",
            new HandoverCreateRequest(Convert.ToBase64String([1])), ProtocolJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/vault/handovers/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Handover_ExpiredRowsArePurgedOnCreate()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        await CreateAsync(owner, [1]);
        f.Clock.Advance(TimeSpan.FromMinutes(11));
        await CreateAsync(owner, [2]);

        await using var conn = f.Services.GetRequiredService<PrivatePasteDb>().Open();
        await using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM handovers";
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }
}
