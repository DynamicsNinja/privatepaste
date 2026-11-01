using PrivatePaste.Protocol;

namespace PrivatePaste.Server.Clips;

public interface IClipBroadcaster
{
    Task ClipChangedAsync(ClipDto clip, bool live);

    Task ClipDeletedAsync(Guid id, long seq);

    /// <summary>Tells every device the encryption key was replaced and the history is gone.</summary>
    Task VaultResetAsync();
}

public sealed class NullClipBroadcaster : IClipBroadcaster
{
    public Task ClipChangedAsync(ClipDto clip, bool live) => Task.CompletedTask;

    public Task ClipDeletedAsync(Guid id, long seq) => Task.CompletedTask;

    public Task VaultResetAsync() => Task.CompletedTask;
}
