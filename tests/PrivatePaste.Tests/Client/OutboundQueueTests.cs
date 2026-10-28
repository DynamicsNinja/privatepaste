using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;

namespace PrivatePaste.Tests.Client;

public class OutboundQueueTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pp-queue-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "queue.enc");
    private static byte[] NewKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    public OutboundQueueTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Queue_survives_restart()
    {
        var key = NewKey();
        var q = new OutboundQueue(FilePath, key);
        q.Enqueue(new ClipboardContent.Text("a"), T0);
        q.Enqueue(new ClipboardContent.Image([1, 2, 3]), T0.AddMinutes(1));

        var loaded = OutboundQueue.Load(FilePath, key);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.TryPeek(out var first));
        Assert.Equal(new ClipboardContent.Text("a"), first.Content);
        Assert.Equal(T0, first.CopiedAt);
        loaded.Dequeue();
        Assert.True(loaded.TryPeek(out var second));
        Assert.Equal(new byte[] { 1, 2, 3 }, ((ClipboardContent.Image)second.Content).Png);
        Assert.Equal(1, OutboundQueue.Load(FilePath, key).Count);
    }

    [Fact]
    public void Corrupt_or_wrong_key_loads_empty()
    {
        var key = NewKey();
        new OutboundQueue(FilePath, key).Enqueue(new ClipboardContent.Text("a"), T0);
        Assert.Equal(0, OutboundQueue.Load(FilePath, NewKey()).Count);
        File.WriteAllBytes(FilePath, [1, 2, 3, 4, 5]);
        Assert.Equal(0, OutboundQueue.Load(FilePath, key).Count);
        Assert.Equal(0, OutboundQueue.Load(Path.Combine(_dir, "missing"), key).Count);
    }

    [Fact]
    public void File_is_not_plaintext()
    {
        var q = new OutboundQueue(FilePath, NewKey());
        q.Enqueue(new ClipboardContent.Text("secret-needle"), T0);
        Assert.DoesNotContain("secret-needle", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(FilePath)));
    }
}
