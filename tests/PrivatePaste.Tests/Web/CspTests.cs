using System.Net;
using System.Text.RegularExpressions;

namespace PrivatePaste.Tests.Web;

public class CspTests
{
    private const string ExpectedCsp =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; img-src 'self' blob: data:; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    private static string Csp(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

    [Theory]
    [InlineData("/login")]
    [InlineData("/")]
    [InlineData("/people")]
    [InlineData("/settings")]
    public async Task EveryPage_SendsCsp(string path)
    {
        using var f = new PrivatePasteFactory();
        await f.LoginAsync("Owner-pc");
        var web = await f.LoginWebAsync();

        var response = await web.GetAsync(path);

        Assert.Equal(ExpectedCsp, Csp(response));
    }

    [Theory]
    [InlineData("/site.js")]
    [InlineData("/health")]
    [InlineData("/no-such-page")]
    public async Task StaticFilesApiAndErrors_SendCsp(string path)
    {
        using var f = new PrivatePasteFactory();
        var response = await f.CreateClient().GetAsync(path);

        Assert.Equal(ExpectedCsp, Csp(response));
    }

    [Fact]
    public async Task NoInlineScriptsOrHandlers()
    {
        using var f = new PrivatePasteFactory();
        await f.LoginAsync("Owner-pc");
        var web = await f.LoginWebAsync();

        foreach (var path in new[] { "/", "/people", "/settings" })
        {
            var html = await web.GetStringAsync(path);
            Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", html);
            Assert.DoesNotMatch(@"\son[a-z]+=", html);
            Assert.DoesNotMatch(@"<style|\sstyle=", html);
        }

        var login = await f.CreateWebClient().GetStringAsync("/login");
        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", login);
        Assert.DoesNotMatch(@"\son[a-z]+=", login);
        Assert.DoesNotMatch(@"<style|\sstyle=", login);
    }
}
