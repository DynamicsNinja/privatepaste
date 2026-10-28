using PrivatePaste.Desktop.Platform;

namespace PrivatePaste.Tests.Client;

public class ClipboardRetryTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task Retry_returns_value_after_transient_failures()
    {
        var calls = 0;
        var result = await ClipboardRetry.RunAsync(() =>
        {
            if (++calls <= 2) throw new ClipboardBusyException();
            return 42;
        }, delay: Fast);
        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Retry_gives_up_quietly_and_logs_once()
    {
        var calls = 0;
        var logs = new List<string>();
        var result = await ClipboardRetry.RunAsync<int>(() => { calls++; throw new ClipboardBusyException(); },
            delay: Fast, log: logs.Add);
        Assert.Equal(0, result);
        Assert.Equal(5, calls);
        Assert.Single(logs);
    }

    [Fact]
    public async Task Retry_does_not_swallow_other_exceptions()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClipboardRetry.RunAsync<int>(
            () => { calls++; throw new InvalidOperationException(); }, delay: Fast));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Giving_up_is_reported_to_the_caller()
    {
        var gaveUp = 0;
        await ClipboardRetry.RunAsync<int>(() => throw new ClipboardBusyException(), delay: Fast, gaveUp: () => gaveUp++);
        Assert.Equal(1, gaveUp);
        await ClipboardRetry.RunAsync(() => 1, delay: Fast, gaveUp: () => gaveUp++);
        Assert.Equal(1, gaveUp);
    }
}
