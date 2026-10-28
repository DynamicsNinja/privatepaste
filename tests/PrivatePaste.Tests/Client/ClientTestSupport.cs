using PrivatePaste.Client.Core.Platform;

namespace PrivatePaste.Tests.Client;

internal sealed class FakeClipboard : IClipboardAccess
{
    public ClipboardContent? Current { get; set; }
    /// <summary>When set, reads return this instead of Current (simulates an OS format conversion).</summary>
    public ClipboardContent? ReadOverride { get; set; }
    public Exception? WriteFailure { get; set; }
    public int Reads { get; private set; }
    public List<ClipboardContent> Writes { get; } = [];
    public event EventHandler? Changed;
    public Task<ClipboardContent?> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(ReadOverride ?? Current); }
    public Task WriteAsync(ClipboardContent content, CancellationToken ct)
    {
        if (WriteFailure is { } f) throw f;
        Writes.Add(content);
        Current = content;
        return Task.CompletedTask;
    }
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
    /// <summary>Status codes returned (empty body) for the next requests, one per request, before reaching the server.</summary>
    public Queue<int> Statuses { get; } = new();
    /// <summary>When set, upload requests wait for this task before being forwarded.</summary>
    public Task? Hold { get; set; }
    public List<string> Uploads { get; } = [];
    private readonly HttpMessageInvoker _inner = new(real, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Offline) throw new HttpRequestException("offline");
        if (Statuses.TryDequeue(out var status)) return new HttpResponseMessage((System.Net.HttpStatusCode)status);
        if (request.RequestUri!.AbsolutePath.StartsWith("/api/clips/") && request.Method == HttpMethod.Post)
        {
            if (request.Content is { } c && request.RequestUri.AbsolutePath.EndsWith("/text"))
                Uploads.Add(await c.ReadAsStringAsync(ct));
            if (Hold is { } h) await h;
        }
        return await _inner.SendAsync(request, ct);
    }
}
