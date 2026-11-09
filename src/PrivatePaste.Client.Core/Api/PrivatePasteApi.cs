using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Api;

/// <summary>
/// Typed client for the PrivatePaste server API. The caller owns the <see cref="HttpClient"/> (BaseAddress = server URL) and
/// should leave its <see cref="HttpClient.Timeout"/> infinite: every call has its own timeout (<see cref="RequestTimeout"/>;
/// uploads get <see cref="UploadTimeout"/> for their size), measured on the injected <see cref="TimeProvider"/>.
/// Exception contract: <see cref="HttpRequestException"/> = offline/transient (network failure, a timeout with the
/// TimeoutException as inner, or a 2xx with an unparseable body such as a captive-portal page); <see cref="UnauthorizedApiException"/> = 401;
/// <see cref="KeyResetException"/> = 409, the upload's vault key was reset; <see cref="UpdateRequiredException"/> = 426;
/// any other <see cref="ApiException"/> = the server rejected the request. Caller cancellation surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class PrivatePasteApi(HttpClient http, TimeProvider? time = null)
{
    private const long UploadBytesPerExtraSecond = 256 * 1024;
    private static readonly TimeSpan BaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxUploadTimeout = TimeSpan.FromMinutes(5);
    private const string KeyResetTitle = "Encryption was reset";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Sent as a Bearer token when set.</summary>
    public string? Token { get; set; }

    /// <summary>Sent as <see cref="ProtocolHeaders.AppVersion"/> on every request when set, so the server can show it.</summary>
    public string? AppVersion { get; init; }

    /// <summary>The timeout of each call that is not an upload.</summary>
    public TimeSpan RequestTimeout { get; init; } = BaseTimeout;

    /// <summary>30 s plus 1 s per started 256 KB of payload, at most 5 min.</summary>
    public static TimeSpan UploadTimeout(long bytes)
    {
        var extraSeconds = (Math.Max(0, bytes) + UploadBytesPerExtraSecond - 1) / UploadBytesPerExtraSecond;
        var maxExtra = (long)(MaxUploadTimeout - BaseTimeout).TotalSeconds;
        return BaseTimeout + TimeSpan.FromSeconds(Math.Min(extraSeconds, maxExtra));
    }

    public Task<LoginResponse> LoginAsync(string username, string password, string deviceName, string platform,
        CancellationToken ct = default) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(HttpMethod.Post, "api/auth/login");
            req.Content = JsonContent.Create(new LoginRequest(password, deviceName, platform) { Username = username },
                options: ProtocolJson.Options);
            return await SendJsonAsync<LoginResponse>(req, t).ConfigureAwait(false);
        });

    public Task LogoutAsync(CancellationToken ct = default) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(HttpMethod.Post, "api/auth/logout");
            using var _ = await SendAsync(req, t).ConfigureAwait(false);
            return true;
        });

    /// <summary>What the server supports and its limits; null when it predates end-to-end encryption (404).</summary>
    public Task<ServerInfoDto?> GetServerInfoAsync(CancellationToken ct = default) =>
        OrNullOn404(GetJsonAsync<ServerInfoDto>("api/server-info", ct));

    public Task<MeDto> GetMeAsync(CancellationToken ct = default) => GetJsonAsync<MeDto>("api/me", ct);

    /// <summary>The wrapped vault key; null when encryption isn't set up yet (404).</summary>
    public Task<VaultWrapDto?> GetVaultAsync(CancellationToken ct = default) =>
        OrNullOn404(GetJsonAsync<VaultWrapDto>("api/vault", ct));

    /// <summary>Sets up encryption, or replaces the wrap of the same key (a new passphrase). Owner only.</summary>
    public Task PutVaultAsync(VaultWrapDto wrap, CancellationToken ct = default) =>
        SendJsonBodyAsync(HttpMethod.Put, "api/vault", wrap, ct);

    /// <summary>Replaces the vault key, which deletes every clip on the server. Owner only.</summary>
    public Task ResetVaultAsync(VaultWrapDto wrap, CancellationToken ct = default) =>
        SendJsonBodyAsync(HttpMethod.Post, "api/vault/reset", wrap, ct);

    /// <summary>Stores a sealed handover blob for a new device to fetch once.</summary>
    public Task<HandoverCreated> CreateHandoverAsync(byte[] blob, CancellationToken ct = default) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(HttpMethod.Post, "api/vault/handovers");
            req.Content = JsonContent.Create(new HandoverCreateRequest(Convert.ToBase64String(blob)), options: ProtocolJson.Options);
            return await SendJsonAsync<HandoverCreated>(req, t).ConfigureAwait(false);
        });

    /// <summary>Fetches (and so uses up) a handover blob; null when it is unknown, expired or already taken (404).</summary>
    public async Task<byte[]?> TakeHandoverAsync(Guid id, CancellationToken ct = default)
    {
        var blob = await OrNullOn404(GetJsonAsync<HandoverBlob>($"api/vault/handovers/{id}", ct)).ConfigureAwait(false);
        if (blob is null) return null;
        try { return Convert.FromBase64String(blob.Blob); }
        catch (FormatException e) { throw new HttpRequestException("Unexpected response from server.", e); }
    }

    /// <summary>
    /// Uploads a sealed clip (see <see cref="Vault.ClipCodec.EncodeAsync"/>) and returns it as stored, still sealed. A
    /// 409 for a key the server no longer has is a <see cref="KeyResetException"/>.
    /// </summary>
    public Task<ClipDto> UploadClipAsync(ClipUpload upload, CancellationToken ct = default) =>
        WithTimeoutAsync(UploadTimeout(upload.Meta.Payload.Length + (upload.Thumb?.Length ?? 0) + upload.Blobs.Sum(b => (long)b.Length)),
            ct, async t =>
            {
                using var form = new MultipartFormDataContent
                {
                    { new StringContent(JsonSerializer.Serialize(upload.Meta, ProtocolJson.Options), Encoding.UTF8, "application/json"), "meta" },
                };
                if (upload.Thumb is { } thumb) form.Add(new ByteArrayContent(thumb), "thumb", "thumb");
                for (var n = 0; n < upload.Blobs.Count; n++)
                {
                    var name = "blob" + n.ToString(CultureInfo.InvariantCulture);
                    form.Add(new ByteArrayContent(upload.Blobs[n]), name, name);
                }
                using var req = Request(HttpMethod.Post, "api/clips");
                req.Content = form;
                return await SendJsonAsync<ClipDto>(req, t).ConfigureAwait(false);
            });

    /// <summary>
    /// A page of history, newest first, still sealed. The server can't search (it holds only ciphertext): see
    /// <see cref="Sync.OlderClipSearch"/>.
    /// </summary>
    public Task<ClipPage> ListAsync(string? cursor, int limit = 50, CancellationToken ct = default)
    {
        var url = $"api/clips?limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor);
        return GetJsonAsync<ClipPage>(url, ct);
    }

    /// <summary>Pinned clips only, newest pinned first; cursors work as in <see cref="ListAsync"/>.</summary>
    public Task<ClipPage> ListPinnedAsync(string? cursor, int limit = 50, CancellationToken ct = default)
    {
        var url = $"api/clips?pinned=true&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor);
        return GetJsonAsync<ClipPage>(url, ct);
    }

    /// <summary>Pins or unpins a clip; returns it as it is now (a 404 for an unknown clip is an <see cref="ApiException"/>).</summary>
    public Task<ClipDto> PinAsync(Guid id, bool pinned, CancellationToken ct = default) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(pinned ? HttpMethod.Post : HttpMethod.Delete, $"api/clips/{id}/pin");
            return await SendJsonAsync<ClipDto>(req, t).ConfigureAwait(false);
        });

    public Task<ClipDto> RestoreAsync(Guid id, CancellationToken ct = default) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(HttpMethod.Post, $"api/clips/{id}/restore");
            return await SendJsonAsync<ClipDto>(req, t).ConfigureAwait(false);
        });

    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(HttpMethod.Delete, $"api/clips/{id}");
            using var _ = await SendAsync(req, t).ConfigureAwait(false);
            return true;
        });

    /// <summary>A clip's sealed thumbnail (<see cref="Vault.ClipCodec.DecodeThumb"/> opens it).</summary>
    public Task<byte[]> GetThumbAsync(Guid id, CancellationToken ct = default) => GetBytesAsync($"api/clips/{id}/thumb", RequestTimeout, ct);

    /// <summary>
    /// A clip's sealed blob <paramref name="n"/> (<see cref="Vault.ClipCodec.DecodeBlobAsync(byte[], CancellationToken)"/> opens it).
    /// With <paramref name="expectedBytes"/> (a file's size) the timeout grows with it as an upload's does
    /// (<see cref="UploadTimeout"/>), so a large file on a slow link isn't cut off.
    /// </summary>
    public Task<byte[]> GetBlobAsync(Guid id, int n, CancellationToken ct = default, long expectedBytes = 0) =>
        GetBytesAsync($"api/clips/{id}/blobs/{n.ToString(CultureInfo.InvariantCulture)}",
            expectedBytes > 0 && UploadTimeout(expectedBytes) is var scaled && scaled > RequestTimeout ? scaled : RequestTimeout, ct);

    public Task<SyncResponse> SyncAsync(long afterSeq, int limit = 200, CancellationToken ct = default) =>
        GetJsonAsync<SyncResponse>(
            $"api/sync?afterSeq={afterSeq.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}", ct);

    /// <summary>
    /// Runs one call (send and body read) under its own timeout. That timeout firing surfaces as an
    /// <see cref="HttpRequestException"/> with a <see cref="TimeoutException"/> inside; the caller's cancellation stays an OCE.
    /// </summary>
    private async Task<T> WithTimeoutAsync<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> call)
    {
        using var timer = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            return await call(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Request timed out.", new TimeoutException($"No answer within {timeout}.", e));
        }
    }

    private Task<T> GetJsonAsync<T>(string url, CancellationToken ct) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(HttpMethod.Get, url);
            return await SendJsonAsync<T>(req, t).ConfigureAwait(false);
        });

    private Task SendJsonBodyAsync<TBody>(HttpMethod method, string url, TBody body, CancellationToken ct) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(method, url);
            req.Content = JsonContent.Create(body, options: ProtocolJson.Options);
            using var _ = await SendAsync(req, t).ConfigureAwait(false);
            return true;
        });

    private static async Task<T?> OrNullOn404<T>(Task<T> call) where T : class
    {
        try { return await call.ConfigureAwait(false); }
        catch (ApiException e) when (e.Status == HttpStatusCode.NotFound) { return null; }
    }

    private Task<byte[]> GetBytesAsync(string url, TimeSpan timeout, CancellationToken ct) =>
        WithTimeoutAsync(timeout, ct, async t =>
        {
            using var req = Request(HttpMethod.Get, url);
            using var res = await SendAsync(req, t).ConfigureAwait(false);
            return await res.Content.ReadAsByteArrayAsync(t).ConfigureAwait(false);
        });

    // Relative URLs without a leading slash so a sub-path in BaseAddress is preserved.
    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        if (Token is { Length: > 0 } t) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        req.Headers.Add(ProtocolHeaders.Name, ProtocolHeaders.Version);
        if (AppVersion is { Length: > 0 } v) req.Headers.TryAddWithoutValidation(ProtocolHeaders.AppVersion, v);
        return req;
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await SendAsync(req, ct).ConfigureAwait(false);
        try
        {
            return await res.Content.ReadFromJsonAsync<T>(ProtocolJson.Options, ct).ConfigureAwait(false)
                ?? throw new HttpRequestException("Unexpected response from server.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new HttpRequestException("Unexpected response from server.", e);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)   // HttpClient.Timeout, if the owner set one
        {
            throw new HttpRequestException("Request timed out.", e.InnerException ?? e);
        }
        if (res.IsSuccessStatusCode) return res;
        try
        {
            var (title, detail) = await ReadProblemAsync(res, ct).ConfigureAwait(false);
            var message = detail ?? title ?? $"{(int)res.StatusCode} {res.ReasonPhrase}";
            throw res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new UnauthorizedApiException(message),
                HttpStatusCode.UpgradeRequired => new UpdateRequiredException(message),
                // The server marks this one by its title; other 409s (no vault yet, a kind clash) are plain rejections.
                HttpStatusCode.Conflict when title == KeyResetTitle => new KeyResetException(message),
                _ => new ApiException(res.StatusCode, message),
            };
        }
        finally { res.Dispose(); }
    }

    /// <summary>The problem details' title and detail, each null when missing or empty, both null when the body isn't JSON.</summary>
    private static async Task<(string? Title, string? Detail)> ReadProblemAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 0)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return (Text(doc.RootElement, "title"), Text(doc.RootElement, "detail"));
            }
        }
        catch (JsonException) { }
        return (null, null);

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;
    }
}
