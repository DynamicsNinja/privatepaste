using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Tests.Client;

internal sealed class FakeClipboard : IClipboardAccess
{
    public ClipboardContent? Current { get; set; }
    public List<ClipboardContent> Writes { get; } = [];
    public event EventHandler? Changed;
    public Task<ClipboardContent?> ReadAsync(CancellationToken ct) => Task.FromResult(Current);
    public Task WriteAsync(ClipboardContent content, CancellationToken ct) { Writes.Add(content); Current = content; return Task.CompletedTask; }
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class ListNotifier : INotifier
{
    public List<string> Messages { get; } = [];
    public void Notify(string message) => Messages.Add(message);
}

/// <summary>Forwards to the real (in-process server) handler, or fails like a dead network when Offline.</summary>
internal sealed class SwitchHandler(HttpMessageHandler real) : HttpMessageHandler
{
    public bool Offline { get; set; }
    private readonly HttpMessageInvoker _inner = new(real, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Offline) throw new HttpRequestException("offline");
        return await _inner.SendAsync(request, ct);
    }
}
