using System.Net;
using PrivatePaste.Server.Auth;

namespace PrivatePaste.Tests.Auth;

public class LoginGuardTests
{
    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:ffff:ffff:ffff:ffff")]
    [InlineData("2001:db8:1:2:aaaa::", "2001:db8:1:2:bbbb:cccc:dddd:eeee")]
    public void Ipv6_addresses_in_the_same_64_share_a_partition(string a, string b) =>
        Assert.Equal(LoginPartition.KeyFor(IPAddress.Parse(a)), LoginPartition.KeyFor(IPAddress.Parse(b)));

    [Fact]
    public void Ipv6_addresses_in_different_64s_do_not_share_a_partition() =>
        Assert.NotEqual(LoginPartition.KeyFor(IPAddress.Parse("2001:db8:1:2::1")),
            LoginPartition.KeyFor(IPAddress.Parse("2001:db8:1:3::1")));

    [Fact]
    public void Ipv4_is_partitioned_by_full_address()
    {
        Assert.Equal("192.0.2.1", LoginPartition.KeyFor(IPAddress.Parse("192.0.2.1")));
        Assert.NotEqual(LoginPartition.KeyFor(IPAddress.Parse("192.0.2.1")), LoginPartition.KeyFor(IPAddress.Parse("192.0.2.2")));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_is_treated_as_ipv4() =>
        Assert.Equal("192.0.2.1", LoginPartition.KeyFor(IPAddress.Parse("::ffff:192.0.2.1")));

    [Fact]
    public void Unknown_address_has_its_own_partition() => Assert.Equal("unknown", LoginPartition.KeyFor(null));

    [Fact]
    public async Task Gate_admits_up_to_the_limit_queues_a_few_and_rejects_the_rest()
    {
        var gate = new ConcurrencyGate(maxConcurrent: 2, maxQueued: 1);
        using var first = await gate.TryEnterAsync();
        var second = await gate.TryEnterAsync();
        Assert.NotNull(first);
        Assert.NotNull(second);

        var queued = gate.TryEnterAsync();
        Assert.False(queued.IsCompleted);

        var rejected = gate.TryEnterAsync();
        Assert.True(rejected.IsCompleted);
        Assert.Null(await rejected);

        second!.Dispose();
        using var third = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(third);

        // The queue slot is free again.
        var queuedAgain = gate.TryEnterAsync();
        Assert.False(queuedAgain.IsCompleted);
        first!.Dispose();
        using var fourth = await queuedAgain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(fourth);
    }

    [Fact]
    public void Password_verifier_allows_two_concurrent_hashes() => Assert.Equal(2, PasswordVerifier.MaxConcurrentHashes);
}
