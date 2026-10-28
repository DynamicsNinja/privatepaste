using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Sync;

namespace PrivatePaste.Tests.Client;

public class EchoGuardTests
{
    [Fact]
    public void Remembered_hash_is_echo_once()
    {
        var g = new EchoGuard(new FakeTimeProvider(), TimeSpan.FromSeconds(30));
        g.Remember("h");
        Assert.True(g.IsEcho("h"));
        Assert.False(g.IsEcho("h"));
    }

    [Fact]
    public void Unknown_hash_is_not_echo() =>
        Assert.False(new EchoGuard(new FakeTimeProvider(), TimeSpan.FromSeconds(30)).IsEcho("x"));

    [Fact]
    public void Expires_after_window()
    {
        var t = new FakeTimeProvider();
        var g = new EchoGuard(t, TimeSpan.FromSeconds(30));
        g.Remember("h");
        t.Advance(TimeSpan.FromSeconds(31));
        Assert.False(g.IsEcho("h"));
    }
}
