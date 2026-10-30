using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Web;

public class PeoplePageTests
{
    private const string AnaPassword = "person-pass-1";

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, string token,
        params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new("__RequestVerificationToken", token))));

    private static async Task<ClipDto> UploadAsync(HttpClient api, string text)
    {
        var response = await api.PostAsJsonAsync("/api/clips/text", new TextClipUpload(text, null, false), ProtocolJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
    }

    private static int Count(string html, string pattern) => Regex.Matches(html, pattern).Count;

    [Fact]
    public async Task People_Owner_SeesEveryoneAndAddForm()
    {
        using var f = new PrivatePasteFactory();
        await f.LoginAsync("Owner-pc");
        await f.AddPersonAsync("Ana");
        await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var web = await f.LoginWebAsync();

        var html = await web.GetStringAsync("/people");
        Assert.Contains("class=\"person-chip\">owner<", html);
        Assert.Contains("class=\"person-chip\">Ana<", html);
        Assert.Contains("Owner-pc", html);
        Assert.Contains("Ana-phone", html);
        Assert.Contains(">Add person</button>", html);
        // Rename on both cards; Reset password and Remove only on Ana's.
        Assert.Equal(2, Count(html, ">Rename</"));
        Assert.Equal(1, Count(html, ">Reset password</"));
        Assert.Equal(1, Count(html, ">Remove</button>"));
        Assert.Matches("data-confirm=\"Remove Ana\\?", html);
        Assert.Equal(2, Count(html, "class=\"ghost\">Sign out</button>")); // one per device, besides the nav's
    }

    [Fact]
    public async Task People_RegularPerson_SeesOnlyOwnCard_NoAddForm()
    {
        using var f = new PrivatePasteFactory();
        await f.LoginAsync("Owner-pc");
        await f.AddPersonAsync("Ana");
        await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var web = await f.LoginWebAsync("Ana", AnaPassword);

        var html = await web.GetStringAsync("/people");
        Assert.Contains("class=\"person-chip\">Ana<", html);
        Assert.DoesNotContain("class=\"person-chip\">owner<", html);
        Assert.DoesNotContain("Owner-pc", html);
        Assert.Contains("Ana-phone", html);
        Assert.DoesNotContain("Add person", html);
        Assert.DoesNotContain(">Rename</", html);
        Assert.DoesNotContain(">Reset password</", html);
        Assert.DoesNotContain(">Remove</", html);
        Assert.Equal(1, Count(html, "class=\"ghost\">Sign out</button>"));
    }

    [Fact]
    public async Task People_RegularPerson_PostRemove_403()
    {
        using var f = new PrivatePasteFactory();
        var ana = await f.AddPersonAsync("Ana");
        var ben = await f.AddPersonAsync("Ben");
        var web = await f.LoginWebAsync("Ana", AnaPassword);
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/people");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostFormAsync(web, "/people?handler=Remove", token, ("id", ben.ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostFormAsync(web, "/people?handler=Add", token, ("name", "Cleo"), ("password", "cleo-pass-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostFormAsync(web, "/people?handler=Rename", token, ("id", ana.ToString()), ("name", "Boss"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostFormAsync(web, "/people?handler=ResetPassword", token, ("id", ben.ToString()), ("password", "new-pass-123"))).StatusCode);
        // Ben is still there and can sign in with his password.
        await f.LoginAsync("Ben-phone", "android", "Ben", AnaPassword);
    }

    [Fact]
    public async Task People_AddDuplicateName_ShowsTakenError()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var web = await f.LoginWebAsync();
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/people");

        var response = await PostFormAsync(web, "/people?handler=Add", token, ("name", "ana"), ("password", "other-pass-1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<p class=\"error\" role=\"alert\">That name is already taken.</p>", html);
        // The typed name stays; the password does not.
        Assert.Contains("value=\"ana\"", html);
        Assert.DoesNotContain("other-pass-1", html);
    }

    [Fact]
    public async Task People_Owner_AddsPerson_WhoCanSignIn()
    {
        using var f = new PrivatePasteFactory();
        var web = await f.LoginWebAsync();
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/people");

        var response = await PostFormAsync(web, "/people?handler=Add", token, ("name", "Cleo"), ("password", "cleo-pass-1"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("class=\"person-chip\">Cleo<", await web.GetStringAsync("/people"));
        await f.LoginAsync("Cleo-pc", "windows", "Cleo", "cleo-pass-1");
    }

    [Fact]
    public async Task People_Owner_ShortPassword_ShowsError()
    {
        using var f = new PrivatePasteFactory();
        var web = await f.LoginWebAsync();
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/people");

        var html = await (await PostFormAsync(web, "/people?handler=Add", token, ("name", "Cleo"), ("password", "short")))
            .Content.ReadAsStringAsync();
        Assert.Contains("Password must be at least 8 characters.", html);
    }

    [Fact]
    public async Task People_Owner_Renames()
    {
        using var f = new PrivatePasteFactory();
        var ana = await f.AddPersonAsync("Ana");
        var web = await f.LoginWebAsync();
        Assert.Contains("value=\"Ana\"", await web.GetStringAsync($"/people?rename={ana}"));
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/people");

        var response = await PostFormAsync(web, "/people?handler=Rename", token, ("id", ana.ToString()), ("name", "Anamarija"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("class=\"person-chip\">Anamarija<", await web.GetStringAsync("/people"));
    }

    [Fact]
    public async Task People_Owner_RenameToTakenName_ShowsErrorOnThatCard()
    {
        using var f = new PrivatePasteFactory();
        var ana = await f.AddPersonAsync("Ana");
        var web = await f.LoginWebAsync();
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/people");

        var response = await PostFormAsync(web, "/people?handler=Rename", token, ("id", ana.ToString()), ("name", "OWNER"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Matches($"id=\"person-{ana}\"(?s).*That name is already taken\\.", html);
        Assert.Contains("value=\"OWNER\"", html);
    }

    [Fact]
    public async Task People_Owner_ResetPassword_SignsOutTheirDevices()
    {
        using var f = new PrivatePasteFactory();
        var ana = await f.AddPersonAsync("Ana");
        var anaDevice = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var web = await f.LoginWebAsync();
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/people");

        var response = await PostFormAsync(web, "/people?handler=ResetPassword", token,
            ("id", ana.ToString()), ("password", "brand-new-pass"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anaDevice.GetAsync("/api/clips")).StatusCode);
        await f.LoginAsync("Ana-phone", "android", "Ana", "brand-new-pass");
    }

    [Fact]
    public async Task People_Owner_Removes()
    {
        using var f = new PrivatePasteFactory();
        var ana = await f.AddPersonAsync("Ana");
        var anaDevice = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var web = await f.LoginWebAsync();
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/people");

        var response = await PostFormAsync(web, "/people?handler=Remove", token, ("id", ana.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("person-chip\">Ana<", await web.GetStringAsync("/people"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anaDevice.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task People_RegularPerson_CanOnlySignOutOwnDevices()
    {
        using var f = new PrivatePasteFactory();
        var (owner, ownerDevice, _) = await f.LoginDeviceAsync("Owner-pc");
        await f.AddPersonAsync("Ana");
        var (ana, anaDevice, _) = await f.LoginDeviceAsync("Ana-phone", "android", "Ana", AnaPassword);
        var web = await f.LoginWebAsync("Ana", AnaPassword);
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/people");

        await PostFormAsync(web, "/people?handler=SignOutDevice", token, ("id", ownerDevice.ToString()));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/clips")).StatusCode);

        var response = await PostFormAsync(web, "/people?handler=SignOutDevice", token, ("id", anaDevice.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Devices_RedirectsToPeople()
    {
        using var f = new PrivatePasteFactory();
        var response = await f.CreateWebClient().GetAsync("/devices");
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/people", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Nav_links_History_People_Settings()
    {
        using var f = new PrivatePasteFactory();
        var html = await (await f.LoginWebAsync()).GetStringAsync("/people");
        Assert.Matches("(?s)<nav>.*href=\"/\"[^>]*>History</a>.*href=\"/people\" aria-current=\"page\">People</a>.*href=\"/settings\"[^>]*>Settings</a>", html);
        Assert.Contains("action=\"/People?handler=SignOut\"", html);
        Assert.DoesNotContain(">Devices</a>", html);
    }

    [Fact]
    public async Task Settings_RegularPerson_NoClearOrAutoDelete_HasChangePassword()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var web = await f.LoginWebAsync("Ana", AnaPassword);

        var html = await web.GetStringAsync("/settings");
        Assert.Contains("<h2>Change password</h2>", html);
        Assert.DoesNotContain("Clear history", html);
        Assert.DoesNotContain("auto-delete", html);

        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/settings");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostFormAsync(web, "/settings?handler=Clear", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostFormAsync(web, "/settings?handler=AutoDelete", token, ("days", "7"))).StatusCode);
    }

    [Fact]
    public async Task Settings_Owner_HasEverySection()
    {
        using var f = new PrivatePasteFactory();
        var html = await (await f.LoginWebAsync()).GetStringAsync("/settings");
        Assert.Contains("<h2>Change password</h2>", html);
        Assert.Contains("id=\"auto-delete\"", html);
        Assert.Contains("<h2>Clear history</h2>", html);
    }

    [Fact]
    public async Task Settings_ChangePassword_ShowsPasswordChanged()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var web = await f.LoginWebAsync("Ana", AnaPassword);
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/settings");

        var response = await PostFormAsync(web, "/settings?handler=ChangePassword", token,
            ("current", AnaPassword), ("password", "ana-new-pass"), ("repeat", "ana-new-pass"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var html = await web.GetStringAsync(response.Headers.Location!.OriginalString);
        Assert.Contains("Password changed.", html);
        // Shown once.
        Assert.DoesNotContain("Password changed.", await web.GetStringAsync("/settings"));
        await f.LoginAsync("Ana-phone", "android", "Ana", "ana-new-pass");
    }

    /// <summary>Changes the password on /settings in <paramref name="web"/> and checks it worked.</summary>
    private static async Task ChangePasswordAsync(HttpClient web, string current, string password)
    {
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/settings");
        var response = await PostFormAsync(web, "/settings?handler=ChangePassword", token,
            ("current", current), ("password", password), ("repeat", password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Password changed.", await web.GetStringAsync(response.Headers.Location!.OriginalString));
    }

    private static async Task AssertSignedOutAsync(HttpClient web)
    {
        var response = await web.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task Settings_OwnerChangesPassword_EndsOtherBrowsers_KeepsThisOneAndDevices()
    {
        using var f = new PrivatePasteFactory();
        var device = await f.LoginAsync("Owner-pc");
        var here = await f.LoginWebAsync();
        var elsewhere = await f.LoginWebAsync();

        await ChangePasswordAsync(here, PrivatePasteFactory.Password, "owner-new-pass");

        await AssertSignedOutAsync(elsewhere);
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Settings_PersonChangesPassword_EndsOtherBrowsers_KeepsThisOneAndDevices()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var device = await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword);
        var here = await f.LoginWebAsync("Ana", AnaPassword);
        var elsewhere = await f.LoginWebAsync("Ana", AnaPassword);

        await ChangePasswordAsync(here, AnaPassword, "ana-new-pass");

        await AssertSignedOutAsync(elsewhere);
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/clips")).StatusCode);
    }

    [Fact]
    public async Task Settings_ChangePassword_IsRateLimitedWithSignIns_OtherPostsAreNot()
    {
        using var f = new PrivatePasteFactory();
        var web = await f.LoginWebAsync(); // the first attempt this minute
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/settings");
        Task<HttpResponseMessage> Attempt() => PostFormAsync(web, "/settings?handler=ChangePassword", token,
            ("current", "wrong-current"), ("password", "owner-new-pass"), ("repeat", "owner-new-pass"));

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.OK, (await Attempt()).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Attempt()).StatusCode);
        // Handler names are matched the way Razor Pages matches them, so a different case is the same attempt.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostFormAsync(web, "/settings?handler=changepassword", token,
            ("current", "wrong-current"), ("password", "owner-new-pass"), ("repeat", "owner-new-pass"))).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect,
            (await PostFormAsync(web, "/settings?handler=AutoDelete", token, ("days", "7"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/settings")).StatusCode);
    }

    [Theory]
    [InlineData("wrong-current", "ana-new-pass", "ana-new-pass", "Current password is wrong.")]
    [InlineData(AnaPassword, "ana-new-pass", "ana-other-pass", "Passwords don&#x27;t match.")]
    [InlineData(AnaPassword, "short", "short", "Password must be at least 8 characters.")]
    public async Task Settings_ChangePassword_ShowsError(string current, string password, string repeat, string error)
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        var web = await f.LoginWebAsync("Ana", AnaPassword);
        var token = await PrivatePasteFactory.AntiforgeryTokenAsync(web, "/settings");

        var response = await PostFormAsync(web, "/settings?handler=ChangePassword", token,
            ("current", current), ("password", password), ("repeat", repeat));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"<p class=\"error\" role=\"alert\">{error}</p>", html);
        Assert.DoesNotContain("Password changed.", html);
        Assert.DoesNotContain(AnaPassword, html);
    }

    [Fact]
    public async Task History_TwoPeople_RowShowsPersonChip()
    {
        using var f = new PrivatePasteFactory();
        await f.AddPersonAsync("Ana");
        await UploadAsync(await f.LoginAsync("Ana-phone", "android", "Ana", AnaPassword), "from ana");
        var html = await (await f.LoginWebAsync()).GetStringAsync("/");
        Assert.Contains("class=\"person-chip\">Ana<", html);
        Assert.Matches("class=\"person-chip\">Ana</span><svg class=\"platform\" data-platform=\"android\"", html);
    }

    [Fact]
    public async Task History_OnePerson_NoPersonChip()
    {
        using var f = new PrivatePasteFactory();
        await UploadAsync(await f.LoginAsync(), "only me");
        var html = await (await f.LoginWebAsync()).GetStringAsync("/");
        Assert.Contains("only me", html);
        Assert.DoesNotContain("person-chip", html);
    }
}
