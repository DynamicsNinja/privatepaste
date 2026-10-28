using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Api;

/// <summary>Typed client for the PrivatePaste server API. The caller owns the <see cref="HttpClient"/> (BaseAddress = server URL).</summary>
public sealed class PrivatePasteApi(HttpClient http)
{
    /// <summary>Sent as a Bearer token when set.</summary>
    public string? Token { get; set; }

    public async Task<LoginResponse> LoginAsync(string password, string deviceName, string platform, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, "api/auth/login");
        req.Content = JsonContent.Create(new LoginRequest(password, deviceName, platform), options: ProtocolJson.Options);
        return await SendJsonAsync<LoginResponse>(req, ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, "api/auth/logout");
        await SendAsync(req, ct);
    }

    public async Task<ClipDto> UploadTextAsync(string text, DateTimeOffset? copiedAt, bool backfill, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, "api/clips/text");
        req.Content = JsonContent.Create(new TextClipUpload(text, copiedAt, backfill), options: ProtocolJson.Options);
        return await SendJsonAsync<ClipDto>(req, ct);
    }

    public async Task<ClipDto> UploadImageAsync(byte[] png, DateTimeOffset? copiedAt, bool backfill, CancellationToken ct = default)
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
        return await SendJsonAsync<ClipDto>(req, ct);
    }

    public async Task<ClipPage> ListAsync(string? query, string? cursor, int limit = 50, CancellationToken ct = default)
    {
        var url = $"api/clips?limit={limit}";
        if (!string.IsNullOrEmpty(query)) url += "&q=" + Uri.EscapeDataString(query);
        if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor);
        using var req = Request(HttpMethod.Get, url);
        return await SendJsonAsync<ClipPage>(req, ct);
    }

    public async Task<ClipDto> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"api/clips/{id}/restore");
        return await SendJsonAsync<ClipDto>(req, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Delete, $"api/clips/{id}");
        await SendAsync(req, ct);
    }

    public Task<byte[]> GetImageAsync(Guid id, CancellationToken ct = default) => GetBytesAsync($"api/clips/{id}/image", ct);

    public Task<byte[]> GetThumbnailAsync(Guid id, CancellationToken ct = default) => GetBytesAsync($"api/clips/{id}/thumbnail", ct);

    public async Task<SyncResponse> SyncAsync(long afterSeq, int limit = 200, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"api/sync?afterSeq={afterSeq}&limit={limit}");
        return await SendJsonAsync<SyncResponse>(req, ct);
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, url);
        using var res = await SendAsync(req, ct);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    // Relative URLs without a leading slash so a sub-path in BaseAddress is preserved.
    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        if (Token is { Length: > 0 } t) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        return req;
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await SendAsync(req, ct);
        return await res.Content.ReadFromJsonAsync<T>(ProtocolJson.Options, ct)
            ?? throw new ApiException(res.StatusCode, "Empty response from server.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var res = await http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return res;
        try
        {
            var message = await ReadMessageAsync(res, ct);
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
            var body = await res.Content.ReadAsStringAsync(ct);
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
