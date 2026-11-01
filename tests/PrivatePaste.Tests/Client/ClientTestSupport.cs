using System.Text.Json;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;

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
    /// <summary>Status codes returned (empty body) for the next clip uploads, one per upload, before reaching the server.</summary>
    public Queue<int> Statuses { get; } = new();
    /// <summary>When set, uploads wait for this task before being forwarded.</summary>
    public Task? Hold { get; set; }
    /// <summary>The vault key uploads are sealed with, so <see cref="Uploads"/> can show their text.</summary>
    public VaultKey? Key { get; set; }
    /// <summary>
    /// Every clip upload that got this far (not offline): its text opened with <see cref="Key"/> ("" for an image), or its
    /// meta JSON without a key or under another key.
    /// </summary>
    public List<string> Uploads { get; } = [];
    /// <summary>The key id of each recorded upload, in order.</summary>
    public List<string> UploadKeyIds { get; } = [];
    /// <summary>When set, an upload whose recorded text matches gets a 503 (a transient failure) instead of reaching the server.</summary>
    public Func<string, bool>? FailTextUpload { get; set; }
    /// <summary>When set and it returns a response, that response is used instead of the server's (runs first).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Respond { get; set; }
    private readonly HttpMessageInvoker _inner = new(real, disposeHandler: false);

    public static bool IsUpload(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/clips";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Respond?.Invoke(request) is { } canned) return canned;
        if (Offline) throw new HttpRequestException("offline");
        if (IsUpload(request))
        {
            var (text, keyId) = await ReadUploadAsync(request, ct);
            lock (Uploads)
            {
                Uploads.Add(text);
                UploadKeyIds.Add(keyId);
            }
            if (Statuses.TryDequeue(out var status)) return new HttpResponseMessage((System.Net.HttpStatusCode)status);
            if (FailTextUpload?.Invoke(text) == true) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            if (Hold is { } h) await h;
        }
        return await _inner.SendAsync(request, ct);
    }

    private async Task<(string Text, string KeyId)> ReadUploadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        foreach (var part in (MultipartFormDataContent)request.Content!)
        {
            if (part.Headers.ContentDisposition?.Name?.Trim('"') != "meta") continue;
            var json = await part.ReadAsStringAsync(ct);
            var meta = JsonSerializer.Deserialize<ClipUploadMeta>(json, ProtocolJson.Options)!;
            if (Key is null || meta.KeyId != Key.KeyId) return (json, meta.KeyId);
            return (ClipPayload.Open(Key, meta.Payload).Text ?? "", meta.KeyId);
        }
        throw new InvalidOperationException("An upload without meta.");
    }
}

/// <summary>Uploads and lists the way the apps do: sealed with a codec, opened before the test looks at them.</summary>
internal static class EncryptedClips
{
    public static readonly ServerLimitsDto Limits = ClipboardCoordinator.DefaultLimits;

    public static async Task<ClipDto> UploadTextAsync(this PrivatePasteApi api, ClipCodec codec, string text,
        DateTimeOffset? copiedAt = null, bool backfill = false) =>
        codec.Decode(await api.UploadClipAsync(
            await codec.EncodeAsync(new ClipboardContent.Text(text), copiedAt, backfill, Limits, default)));

    public static async Task<ClipDto> UploadImageAsync(this PrivatePasteApi api, ClipCodec codec, byte[] image,
        DateTimeOffset? copiedAt = null, bool backfill = false) =>
        codec.Decode(await api.UploadClipAsync(
            await codec.EncodeAsync(new ClipboardContent.Image(image), copiedAt, backfill, Limits, default)));

    public static async Task<ClipPage> ListOpenedAsync(this PrivatePasteApi api, ClipCodec codec, string? cursor = null,
        int limit = 50)
    {
        var page = await api.ListAsync(cursor, limit);
        return page with { Items = page.Items.Select(codec.Decode).ToList() };
    }

    public static async Task<ClipPage> ListPinnedOpenedAsync(this PrivatePasteApi api, ClipCodec codec, string? cursor = null,
        int limit = 50)
    {
        var page = await api.ListPinnedAsync(cursor, limit);
        return page with { Items = page.Items.Select(codec.Decode).ToList() };
    }
}
