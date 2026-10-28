using System.Net;

namespace PrivatePaste.Tests;

public class HealthTests
{
    [Fact]
    public async Task Health_returns_200_without_auth()
    {
        using var f = new PrivatePasteFactory();
        var r = await f.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
