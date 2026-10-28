using System.Globalization;
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
/// any other <see cref="ApiException"/> = the server rejected the request. Caller cancellation surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class PrivatePasteApi(HttpClient http, TimeProvider? time = null)
{
    private const long UploadBytesPerExtraSecond = 256 * 1024;
    private static readonly TimeSpan BaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxUploadTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Sent as a Bearer token when set.</summary>
    public string? Token { get; set; }

    /// <summary>The timeout of each call that is not an upload.</summary>
    public TimeSpan RequestTimeout { get; init; } = BaseTimeout;

    /// <summary>30 s plus 1 s per started 256 KB of payload, at most 5 min.</summary>
    public static TimeSpan UploadTimeout(long bytes)
    {
        var extraSeconds = (Math.Max(0, bytes) + UploadBytesPerExtraSecond - 1) / UploadBytesPerExtraSecond;
        var maxExtra = (long)(MaxUploadTimeout - BaseTimeout).TotalSeconds;
        return BaseTimeout + TimeSpan.FromSeconds(Math.Min(extraSeconds, maxExtra));
    }

    public Task<LoginResponse> LoginAsync(string password, string deviceName, string platform, CancellationToken ct = default) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(HttpMethod.Post, "api/auth/login");
            req.Content = JsonContent.Create(new LoginRequest(password, deviceName, platform), options: ProtocolJson.Options);
            return await SendJsonAsync<LoginResponse>(req, t).ConfigureAwait(false);
        });

    public Task LogoutAsync(CancellationToken ct = default) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
        {
            using var req = Request(HttpMethod.Post, "api/auth/logout");
            using var _ = await SendAsync(req, t).ConfigureAwait(false);
            return true;
        });

    public Task<ClipDto> UploadTextAsync(string text, DateTimeOffset? copiedAt, bool backfill, CancellationToken ct = default) =>
        WithTimeoutAsync(UploadTimeout(Encoding.UTF8.GetByteCount(text)), ct, async t =>
        {
            using var req = Request(HttpMethod.Post, "api/clips/text");
            req.Content = JsonContent.Create(new TextClipUpload(text, copiedAt, backfill), options: ProtocolJson.Options);
            return await SendJsonAsync<ClipDto>(req, t).ConfigureAwait(false);
        });

    public Task<ClipDto> UploadImageAsync(byte[] png, DateTimeOffset? copiedAt, bool backfill, CancellationToken ct = default) =>
        WithTimeoutAsync(UploadTimeout(png.Length), ct, async t =>
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "clip.png");
            if (copiedAt is { } at)
                form.Add(new StringContent(at.ToString("O", CultureInfo.InvariantCulture)), "copiedAt");
            form.Add(new StringContent(backfill ? "true" : "false"), "backfill");
            using var req = Request(HttpMethod.Post, "api/clips/image");
            req.Content = form;
            return await SendJsonAsync<ClipDto>(req, t).ConfigureAwait(false);
        });

    public Task<ClipPage> ListAsync(string? query, string? cursor, int limit = 50, CancellationToken ct = default)
    {
        var url = $"api/clips?limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(query)) url += "&q=" + Uri.EscapeDataString(query);
        if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor);
        return GetJsonAsync<ClipPage>(url, ct);
    }

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

    public Task<byte[]> GetImageAsync(Guid id, CancellationToken ct = default) => GetBytesAsync($"api/clips/{id}/image", ct);

    public Task<byte[]> GetThumbnailAsync(Guid id, CancellationToken ct = default) => GetBytesAsync($"api/clips/{id}/thumbnail", ct);

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

    private Task<byte[]> GetBytesAsync(string url, CancellationToken ct) =>
        WithTimeoutAsync(RequestTimeout, ct, async t =>
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
            var message = await ReadMessageAsync(res, ct).ConfigureAwait(false);
            throw res.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? new UnauthorizedApiException(message)
                : new ApiException(res.StatusCode, message);
        }
        finally { res.Dispose(); }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 0)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "detail", "title" })
                        if (doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                            && p.GetString() is { Length: > 0 } s)
                            return s;
                }
            }
        }
        catch (JsonException) { }
        return $"{(int)res.StatusCode} {res.ReasonPhrase}";
    }
}
