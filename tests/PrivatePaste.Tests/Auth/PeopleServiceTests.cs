using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Auth;
using PrivatePaste.Server.Storage;
using PrivatePaste.Tests.Realtime;

namespace PrivatePaste.Tests.Auth;

public class PeopleServiceTests
{
    private const string AnaPassword = "ana-pass-123";

    private static PeopleService People(PrivatePasteFactory f) => f.Services.GetRequiredService<PeopleService>();

    private static async Task<Guid> AddAsync(PrivatePasteFactory f, string name, string password = AnaPassword)
    {
        var result = await People(f).AddAsync(name, password);
        Assert.Equal(new PeopleResult(true, null), result);
        return (await f.Services.GetRequiredService<UserRepository>().FindCurrentByNameAsync(name))!.Id;
    }

    private static async Task<Guid> OwnerIdAsync(PrivatePasteFactory f) =>
        (await f.Services.GetRequiredService<UserRepository>().GetOwnerAsync())!.Id;

    private static async Task<ClipDto> PostAsync(HttpClient c, string text)
    {
        var r = await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, false), ProtocolJson.Options);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    private static async Task<SyncResponse> SyncAsync(HttpClient c, long afterSeq) =>
        (await c.GetFromJsonAsync<SyncResponse>($"/api/sync?afterSeq={afterSeq}", ProtocolJson.Options))!;

    private static async Task<HttpStatusCode> LoginStatusAsync(PrivatePasteFactory f, string name, string password)
    {
        var response = await f.CreateClient().PostAsJsonAsync("/api/auth/login",
            new LoginRequest(password, "Probe", "windows") { Username = name }, ProtocolJson.Options);
        return response.StatusCode;
    }

    [Fact]
    public async Task AddPerson_CanSignIn()
    {
        using var f = new PrivatePasteFactory();
        await AddAsync(f, "Ana");

        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);

        Assert.Equal(HttpStatusCode.OK, (await ana.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task AddPerson_NameDifferingOnlyInCaseOrSpaces_IsTaken()
    {
        using var f = new PrivatePasteFactory();
        await AddAsync(f, "Ana");

        Assert.Equal(new PeopleResult(false, "That name is already taken."), await People(f).AddAsync("  ANA ", AnaPassword));
    }

    [Fact]
    public async Task AddPerson_ShortPassword_Rejected()
    {
        using var f = new PrivatePasteFactory();
        Assert.Equal(new PeopleResult(false, "Password must be at least 8 characters."),
            await People(f).AddAsync("Ana", "short"));
        Assert.Null(await f.Services.GetRequiredService<UserRepository>().FindCurrentByNameAsync("Ana"));
    }

    [Fact]
    public async Task AddPerson_NameOf33Chars_Rejected()
    {
        using var f = new PrivatePasteFactory();
        Assert.Equal(new PeopleResult(false, "Name must be 1-32 characters."),
            await People(f).AddAsync(new string('a', 33), AnaPassword));
        Assert.True((await People(f).AddAsync(new string('a', 32), AnaPassword)).Ok);
    }

    [Fact]
    public async Task AddPerson_WhenHashingIsBusy_TooManyAttempts()
    {
        using var f = new PrivatePasteFactory();
        var gate = new ConcurrencyGate(maxConcurrent: 1, maxQueued: 0);
        using var held = await gate.TryEnterAsync();
        var people = ActivatorUtilities.CreateInstance<PeopleService>(f.Services, new PasswordVerifier(gate));

        Assert.Equal(new PeopleResult(false, "Too many attempts. Wait a minute and try again."),
            await people.AddAsync("Ana", AnaPassword));
    }

    [Fact]
    public async Task RemovedName_CanBeReused()
    {
        using var f = new PrivatePasteFactory();
        var ana = await AddAsync(f, "Ana");
        Assert.True((await People(f).RemoveAsync(ana)).Ok);

        var again = await AddAsync(f, "ana");

        Assert.NotEqual(ana, again);
    }

    [Fact]
    public async Task RemovePerson_TokensRejected()
    {
        using var f = new PrivatePasteFactory();
        var id = await AddAsync(f, "Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var owner = await f.LoginAsync();

        Assert.True((await People(f).RemoveAsync(id)).Ok);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.GetAsync("/api/clips")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/clips")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(f, "Ana", AnaPassword));
    }

    [Fact]
    public async Task RemovePerson_ClosesTheirOpenWebSocket()
    {
        using var f = new PrivatePasteFactory();
        var id = await AddAsync(f, "Ana");
        var (_, _, token) = await f.LoginDeviceAsync("Ana-phone", "android", "Ana", AnaPassword);
        using var socket = await WebSocketTests.ConnectAsync(f, token);

        await People(f).RemoveAsync(id);

        var (result, _) = await WebSocketTests.ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal("revoked", result.CloseStatusDescription);
    }

    [Fact]
    public async Task RemovePerson_PastClipsKeepTheirName()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        var anaId = await AddAsync(f, "Ana");
        await AddAsync(f, "Bo");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var clip = await PostAsync(ana, "from ana");

        await People(f).RemoveAsync(anaId);

        var after = (await owner.GetFromJsonAsync<ClipDto>($"/api/clips/{clip.Id}", ProtocolJson.Options))!;
        Assert.Equal("Ana", after.SourceUserName);
    }

    [Fact]
    public async Task RemoveOwner_Refused()
    {
        using var f = new PrivatePasteFactory();
        var ownerId = await OwnerIdAsync(f);

        var result = await People(f).RemoveAsync(ownerId);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.NotNull(await f.Services.GetRequiredService<UserRepository>().GetOwnerAsync());
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(f, PrivatePasteFactory.OwnerName, PrivatePasteFactory.Password));
    }

    [Fact]
    public async Task ResetPassword_SignsOutTheirDevices_NewPasswordWorks()
    {
        using var f = new PrivatePasteFactory();
        var id = await AddAsync(f, "Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var stampBefore = (await f.Services.GetRequiredService<UserRepository>().GetAsync(id))!.SecurityStamp;

        Assert.True((await People(f).ResetPasswordAsync(id, "brand-new-pass")).Ok);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.GetAsync("/api/clips")).StatusCode);
        Assert.NotEqual(stampBefore, (await f.Services.GetRequiredService<UserRepository>().GetAsync(id))!.SecurityStamp);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(f, "Ana", "brand-new-pass"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(f, "Ana", AnaPassword));
    }

    [Fact]
    public async Task ResetPassword_Owner_Refused()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();

        Assert.Equal(new PeopleResult(false, "Use Change password in Settings."),
            await People(f).ResetPasswordAsync(await OwnerIdAsync(f), "brand-new-pass"));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task ChangeOwnPassword_KeepsDevices()
    {
        using var f = new PrivatePasteFactory();
        var id = await AddAsync(f, "Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var stampBefore = (await f.Services.GetRequiredService<UserRepository>().GetAsync(id))!.SecurityStamp;

        Assert.Equal(new PeopleResult(true, null),
            await People(f).ChangeOwnPasswordAsync(id, AnaPassword, "changed-pass-1", "changed-pass-1"));

        Assert.Equal(HttpStatusCode.OK, (await ana.GetAsync("/api/clips")).StatusCode);
        Assert.Equal(stampBefore, (await f.Services.GetRequiredService<UserRepository>().GetAsync(id))!.SecurityStamp);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(f, "Ana", "changed-pass-1"));
    }

    [Fact]
    public async Task ChangeOwnPassword_WrongCurrent_Rejected()
    {
        using var f = new PrivatePasteFactory();
        var id = await AddAsync(f, "Ana");

        Assert.Equal(new PeopleResult(false, "Current password is wrong."),
            await People(f).ChangeOwnPasswordAsync(id, "not-my-password", "changed-pass-1", "changed-pass-1"));
        Assert.Equal(new PeopleResult(false, "Passwords don't match."),
            await People(f).ChangeOwnPasswordAsync(id, AnaPassword, "changed-pass-1", "changed-pass-2"));
        Assert.Equal(new PeopleResult(false, "Password must be at least 8 characters."),
            await People(f).ChangeOwnPasswordAsync(id, AnaPassword, "short", "short"));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(f, "Ana", AnaPassword));
    }

    [Fact]
    public async Task ChangeOwnPassword_Owner_EndsLegacyWebSessions()
    {
        using var f = new PrivatePasteFactory();
        var settings = f.Services.GetRequiredService<SettingsRepository>();
        Assert.True(await settings.GetLegacyWebSessionsAllowedAsync());

        Assert.True((await People(f).ChangeOwnPasswordAsync(await OwnerIdAsync(f), PrivatePasteFactory.Password,
            "owner-new-pass", "owner-new-pass")).Ok);

        Assert.False(await settings.GetLegacyWebSessionsAllowedAsync());
    }

    [Fact]
    public async Task ChangeOwnPassword_NotOwner_KeepsLegacyWebSessions()
    {
        using var f = new PrivatePasteFactory();
        var id = await AddAsync(f, "Ana");

        Assert.True((await People(f).ChangeOwnPasswordAsync(id, AnaPassword, "changed-pass-1", "changed-pass-1")).Ok);

        Assert.True(await f.Services.GetRequiredService<SettingsRepository>().GetLegacyWebSessionsAllowedAsync());
    }

    [Fact]
    public async Task Rename_ResendsTheirClips_WithNewName()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        var id = await AddAsync(f, "Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var anaClip = await PostAsync(ana, "from ana");
        var ownerClip = await PostAsync(owner, "from owner");
        var anaLater = await PostAsync(ana, "from ana, later");
        var before = await SyncAsync(owner, 0);

        Assert.True((await People(f).RenameAsync(id, " Anna ")).Ok);

        var after = await SyncAsync(owner, before.LatestSeq);
        Assert.Equal([anaClip.Id, anaLater.Id], after.Clips.Select(c => c.Id));
        Assert.All(after.Clips, c => Assert.Equal("Anna", c.SourceUserName));
        Assert.Equal(anaClip.LastCopiedAt, after.Clips[0].LastCopiedAt);
        // Only her clips move: the owner's keeps its seq.
        var ownerNow = (await owner.GetFromJsonAsync<ClipDto>($"/api/clips/{ownerClip.Id}", ProtocolJson.Options))!;
        Assert.Equal(ownerClip.Seq, ownerNow.Seq);
    }

    [Fact]
    public async Task Rename_ToTakenName_Rejected()
    {
        using var f = new PrivatePasteFactory();
        var id = await AddAsync(f, "Ana");

        Assert.Equal(new PeopleResult(false, "That name is already taken."), await People(f).RenameAsync(id, "OWNER"));
        Assert.Equal(new PeopleResult(false, "Name must be 1-32 characters."), await People(f).RenameAsync(id, "   "));
    }

    [Fact]
    public async Task Rename_SameName_ResendsNothing()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        var id = await AddAsync(f, "Ana");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        await PostAsync(ana, "from ana");
        var before = await SyncAsync(owner, 0);

        Assert.True((await People(f).RenameAsync(id, "Ana")).Ok);

        Assert.Empty((await SyncAsync(owner, before.LatestSeq)).Clips);
    }

    [Fact]
    public async Task SecondPersonAdded_ResendsAllClips_WithNames()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        var clip = await PostAsync(owner, "from owner");
        Assert.Null(clip.SourceUserName);
        var before = await SyncAsync(owner, 0);

        await AddAsync(f, "Ana");

        var resent = Assert.Single((await SyncAsync(owner, before.LatestSeq)).Clips);
        Assert.Equal(clip.Id, resent.Id);
        Assert.Equal("owner", resent.SourceUserName);
    }

    [Fact]
    public async Task ThirdPersonAdded_ResendsNothing()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        await AddAsync(f, "Ana");
        await PostAsync(owner, "from owner");
        var before = await SyncAsync(owner, 0);

        await AddAsync(f, "Bo");

        Assert.Empty((await SyncAsync(owner, before.LatestSeq)).Clips);
    }

    [Fact]
    public async Task BackToOnePerson_ResendsAllClips_WithoutNames()
    {
        using var f = new PrivatePasteFactory();
        var owner = await f.LoginAsync();
        var anaId = await AddAsync(f, "Ana");
        var boId = await AddAsync(f, "Bo");
        var ana = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var anaClip = await PostAsync(ana, "from ana");
        var ownerClip = await PostAsync(owner, "from owner");

        // Three to two: names still mean something, nothing is re-sent.
        var beforeBo = await SyncAsync(owner, 0);
        await People(f).RemoveAsync(boId);
        Assert.Empty((await SyncAsync(owner, beforeBo.LatestSeq)).Clips);

        // Two to one: Bo's row is still there (removed), but must not count.
        await People(f).RemoveAsync(anaId);

        var after = await SyncAsync(owner, beforeBo.LatestSeq);
        Assert.Equal([anaClip.Id, ownerClip.Id], after.Clips.Select(c => c.Id));
        Assert.All(after.Clips, c => Assert.Null(c.SourceUserName));
    }

    [Fact]
    public async Task UnknownPerson_Refused()
    {
        using var f = new PrivatePasteFactory();
        var nobody = Guid.NewGuid();

        Assert.False((await People(f).RenameAsync(nobody, "Zed")).Ok);
        Assert.False((await People(f).ResetPasswordAsync(nobody, "brand-new-pass")).Ok);
        Assert.False((await People(f).RemoveAsync(nobody)).Ok);
        Assert.False((await People(f).ChangeOwnPasswordAsync(nobody, "x", "brand-new-pass", "brand-new-pass")).Ok);
    }
}
