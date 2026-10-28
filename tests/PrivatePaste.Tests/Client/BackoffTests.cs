using PrivatePaste.Client.Core.Sync;

namespace PrivatePaste.Tests.Client;

public class BackoffTests
{
    private static void AssertWithin(double expectedSeconds, TimeSpan actual) =>
        Assert.InRange(actual.TotalSeconds, expectedSeconds * 0.8, expectedSeconds * 1.2);

    [Fact]
    public void Sequence_doubles_to_cap_with_jitter()
    {
        var b = new Backoff(new Random(1));
        foreach (var expected in new double[] { 1, 2, 4, 8, 16, 30, 30 })
            AssertWithin(expected, b.Next());
    }

    [Fact]
    public void Reset_starts_again_at_one_second()
    {
        var b = new Backoff(new Random(2));
        for (var i = 0; i < 5; i++) b.Next();
        b.Reset();
        AssertWithin(1, b.Next());
        AssertWithin(2, b.Next());
    }

    [Fact]
    public void Jitter_uses_the_injected_random()
    {
        var a = new Backoff(new Random(7));
        var b = new Backoff(new Random(7));
        for (var i = 0; i < 7; i++) Assert.Equal(a.Next(), b.Next());

        // Over many draws the jitter spans both sides of the base delay.
        var c = new Backoff(new Random(3));
        var firsts = Enumerable.Range(0, 50).Select(_ => { c.Reset(); return c.Next().TotalSeconds; }).ToList();
        Assert.Contains(firsts, s => s < 1);
        Assert.Contains(firsts, s => s > 1);
    }
}
