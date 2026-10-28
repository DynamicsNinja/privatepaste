using PrivatePaste.Protocol;

namespace PrivatePaste.Server.Clips;

public interface IClipBroadcaster
{
    Task ClipChangedAsync(ClipDto clip, bool live);

    Task ClipDeletedAsync(Guid id, long seq);
}

public sealed class NullClipBroadcaster : IClipBroadcaster
{
    public Task ClipChangedAsync(ClipDto clip, bool live) => Task.CompletedTask;

    public Task ClipDeletedAsync(Guid id, long seq) => Task.CompletedTask;
}
