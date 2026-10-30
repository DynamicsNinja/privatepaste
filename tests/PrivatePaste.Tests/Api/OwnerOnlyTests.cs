using System.Net;
using System.Net.Http.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Api;

public class OwnerOnlyTests
{
    private static async Task<ClipDto> PostAsync(HttpClient c, string text)
    {
        var r = await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, false), ProtocolJson.Options);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    [Fact]
    public async Task RegularPerson_ClearHistory_403()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.PostAsync("/api/history/clear", null)).StatusCode);
    }

    [Fact]
    public async Task RegularPerson_PutSettings_403()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");
        var r = await ana.PutAsJsonAsync("/api/settings", new SettingsDto(30), ProtocolJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task RegularPerson_GetSettings_200()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");
        Assert.Equal(HttpStatusCode.OK, (await ana.GetAsync("/api/settings")).StatusCode);
    }

    [Fact]
    public async Task RegularPerson_ListDevices_OnlyOwn()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync("Owner-pc");
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");

        var anas = (await ana.GetFromJsonAsync<List<DeviceDto>>("/api/devices", ProtocolJson.Options))!;
        var owners = (await owner.GetFromJsonAsync<List<DeviceDto>>("/api/devices", ProtocolJson.Options))!;
        Assert.Equal("Ana-phone", Assert.Single(anas).Name);
        Assert.Equal(2, owners.Count);
    }

    [Fact]
    public async Task RegularPerson_RevokeOthersDevice_404_DeviceStillWorks()
    {
        using var f = new PrivatePasteFactory();
        var (owner, ownerDevice, _) = await f.LoginDeviceAsync("Owner-pc");
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");

        Assert.Equal(HttpStatusCode.NotFound, (await ana.DeleteAsync($"/api/devices/{ownerDevice}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/settings")).StatusCode);
    }

    [Fact]
    public async Task RegularPerson_RevokeOwnDevice_204()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var (ana, anaDevice, _) = await f.LoginDeviceAsync("Ana-phone", "android", "Ana", "person-pass-1");
        Assert.Equal(HttpStatusCode.NoContent, (await ana.DeleteAsync($"/api/devices/{anaDevice}")).StatusCode);
    }

    [Fact]
    public async Task RegularPerson_PinDeleteRestore_Allowed()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", "person-pass-1");
        var clip = await PostAsync(ana, "hello");

        Assert.True((await ana.PostAsync($"/api/clips/{clip.Id}/pin", null)).IsSuccessStatusCode);
        // Restore re-copies an existing clip, so it comes before the delete.
        Assert.True((await ana.PostAsync($"/api/clips/{clip.Id}/restore", null)).IsSuccessStatusCode);
        Assert.True((await ana.DeleteAsync($"/api/clips/{clip.Id}")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Owner_ClearHistory_200()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync("Owner-pc");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/history/clear", null)).StatusCode);
    }
}
