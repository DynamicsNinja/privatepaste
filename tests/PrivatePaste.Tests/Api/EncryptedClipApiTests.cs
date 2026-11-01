using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Api;

public class EncryptedClipApiTests
{
    private static async Task<ProblemDetails> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;

    [Fact]
    public async Task UploadText_StoresCiphertextOnly()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);

        var clip = await f.UploadTextAsync(c, key, "secret value 123");

        Assert.Null(clip.Text);
        Assert.Equal(key.KeyId, clip.KeyId);
        Assert.Equal("secret value 123", ClipPayload.Open(key, clip.Payload!).Text);
        var stored = await f.Services.GetRequiredService<ClipRepository>().GetAsync(clip.Id);
        Assert.NotNull(stored!.Payload);
        Assert.Null(stored.Text);

        var needle = Encoding.UTF8.GetBytes("secret value 123");
        foreach (var file in Directory.GetFiles(f.DataDir, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(bytes);
            }
            Assert.True(bytes.AsSpan().IndexOf(needle) < 0, $"{file} holds the plaintext");
        }
    }

    [Fact]
    public async Task UploadImage_RoundTripsBlobAndThumb()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var image = new byte[3 * 1024 * 1024 + 17];
        Random.Shared.NextBytes(image);

        var (clip, blob, thumb) = await f.UploadImageAsync(c, key, image);

        Assert.Equal(ClipKind.Image, clip.Kind);
        Assert.True(clip.HasThumb);
        Assert.Equal(1, clip.BlobCount);
        Assert.Equal(image.Length, clip.SizeBytes);
        var blobResponse = await c.GetAsync($"/api/clips/{clip.Id}/blobs/0");
        Assert.Equal("application/octet-stream", blobResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(blob, await blobResponse.Content.ReadAsByteArrayAsync());
        var thumbResponse = await c.GetAsync($"/api/clips/{clip.Id}/thumb");
        Assert.Equal("application/octet-stream", thumbResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(thumb, await thumbResponse.Content.ReadAsByteArrayAsync());

        using var opened = new MemoryStream();
        await BlobCipher.DecryptAsync(key, new MemoryStream(blob), opened, CancellationToken.None);
        Assert.Equal(image, opened.ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}/blobs/1")).StatusCode);
    }

    [Fact]
    public async Task Thumb_and_blob_are_404_for_a_text_clip_and_an_unknown_clip()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var clip = await f.UploadTextAsync(c, key, "hello");

        Assert.False(clip.HasThumb);
        Assert.Equal(0, clip.BlobCount);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}/thumb")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{clip.Id}/blobs/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{Guid.NewGuid()}/blobs/0")).StatusCode);
    }

    [Fact]
    public async Task WrongKeyId_Is409()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        using var oldKey = VaultKey.Create();

        var response = await PrivatePasteFactory.PostClipAsync(c, PrivatePasteFactory.TextMeta(oldKey, "stale"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Encryption was reset", (await ProblemAsync(response)).Title);
        Assert.Empty((await c.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options))!.Items);
    }

    [Fact]
    public async Task NoVault_Is409()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = VaultKey.Create();

        var response = await PrivatePasteFactory.PostClipAsync(c, PrivatePasteFactory.TextMeta(key, "too early"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Set up encryption first.", (await ProblemAsync(response)).Detail);
    }

    [Fact]
    public async Task UnknownKind_Is400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);

        var meta = PrivatePasteFactory.TextMeta(key, "rich") with { Kind = "rich" };

        Assert.Equal(HttpStatusCode.BadRequest, (await PrivatePasteFactory.PostClipAsync(c, meta)).StatusCode);
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n")]
    [InlineData("abc")]
    [InlineData("../../../../etc/passwd")]
    public async Task Invalid_hash_is_400(string hash)
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);

        var meta = PrivatePasteFactory.TextMeta(key, "x") with { Hash = hash };

        Assert.Equal(HttpStatusCode.BadRequest, (await PrivatePasteFactory.PostClipAsync(c, meta)).StatusCode);
    }

    [Fact]
    public async Task Payload_that_is_not_base64_is_400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);

        var meta = PrivatePasteFactory.TextMeta(key, "x") with { Payload = "not base64!" };

        Assert.Equal(HttpStatusCode.BadRequest, (await PrivatePasteFactory.PostClipAsync(c, meta)).StatusCode);
    }

    [Fact]
    public async Task OversizedText_Is413()
    {
        using var f = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_TEXT_KB"] = "1" });
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);

        // The limit the user sees is the plaintext's UTF-8 size, which the client declares.
        var over = await PrivatePasteFactory.PostClipAsync(c, PrivatePasteFactory.TextMeta(key, new string('a', 1025)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        var declaredOver = PrivatePasteFactory.TextMeta(key, "short") with { SizeBytes = 1025 };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PrivatePasteFactory.PostClipAsync(c, declaredOver)).StatusCode);
        // A payload past the worst case for the limit is refused even when the declared size is small.
        var payloadOver = PrivatePasteFactory.TextMeta(key, "short") with
        {
            Payload = Convert.ToBase64String(new byte[6 * 1024 + 4096 + 1]),
        };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PrivatePasteFactory.PostClipAsync(c, payloadOver)).StatusCode);
        await f.UploadTextAsync(c, key, new string('a', 1024));
    }

    [Theory]
    [InlineData("中", 341)] // 3 UTF-8 bytes each, sealed as \uXXXX: 1023 bytes
    [InlineData("\"", 1024)] // 1 byte each, but 6 once JSON escapes it: the worst case
    [InlineData("č", 512)] // Croatian, 2 bytes each
    public async Task Text_just_under_the_limit_is_accepted_however_the_JSON_escapes_it(string unit, int count)
    {
        using var f = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_TEXT_KB"] = "1" });
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var text = string.Concat(Enumerable.Repeat(unit, count));

        var clip = await f.UploadTextAsync(c, key, text);

        Assert.Equal(text, ClipPayload.Open(key, clip.Payload!).Text);
    }

    [Fact]
    public async Task Maximal_image_upload_fits_the_request_body_limit()
    {
        using var f = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_IMAGE_MB"] = "1" });
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var (meta, _, _) = await PrivatePasteFactory.SealImageAsync(key, [1, 2, 3, 4]);
        meta = meta with { Payload = Convert.ToBase64String(new byte[4096]) };
        var blob = new byte[1024 * 1024 + 9 + 16];
        var thumb = new byte[1024 * 1024];
        var form = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(meta, ProtocolJson.Options), Encoding.UTF8, "application/json"), "meta" },
            { new ByteArrayContent(thumb), "thumb", "thumb" },
            { new ByteArrayContent(blob), "blob0", "blob0" },
        };
        var bodyLength = (await form.ReadAsByteArrayAsync()).Length;

        // The test server doesn't enforce Kestrel's limit, so check the configured one covers this body.
        var limit = f.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize;
        Assert.True(limit >= bodyLength, $"limit {limit} < body {bodyLength}");
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/clips", form)).StatusCode);
    }

    [Fact]
    public async Task Same_hash_under_another_kind_is_409()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var text = await f.UploadTextAsync(c, key, "first as text");
        var (meta, blob, thumb) = await PrivatePasteFactory.SealImageAsync(key, [1, 2, 3, 4]);

        var response = await PrivatePasteFactory.PostClipAsync(c, meta with { Hash = text.Hash }, thumb, blob);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("That content was already stored as a different kind.", (await ProblemAsync(response)).Detail);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.DataDir, "blobs")));
        Assert.Equal(ClipKind.Text, (await c.GetFromJsonAsync<ClipDto>($"/api/clips/{text.Id}", ProtocolJson.Options))!.Kind);
    }

    [Fact]
    public async Task Text_hash_uploaded_as_an_image_is_409()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var (imageMeta, blob, thumb) = await PrivatePasteFactory.SealImageAsync(key, [1, 2, 3, 4]);
        var image = (await (await PrivatePasteFactory.PostClipAsync(c, imageMeta, thumb, blob))
            .Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;

        var response = await PrivatePasteFactory.PostClipAsync(c,
            PrivatePasteFactory.TextMeta(key, "now as text") with { Hash = image.Hash });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("That content was already stored as a different kind.", (await ProblemAsync(response)).Detail);
        Assert.Equal(ClipKind.Image, (await c.GetFromJsonAsync<ClipDto>($"/api/clips/{image.Id}", ProtocolJson.Options))!.Kind);
    }

    // Platforms can disagree on whether content is a link (an underscore in the host, say), and both send the same
    // keyed hash; the second upload must still bump, not fail.
    [Theory]
    [InlineData("text", "link")]
    [InlineData("link", "text")]
    public async Task Same_hash_as_text_and_link_bumps_and_keeps_the_stored_kind(string storedKind, string uploadKind)
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var meta = PrivatePasteFactory.TextMeta(key, "https://my_host.example/a");
        var first = (await (await PrivatePasteFactory.PostClipAsync(c, meta with { Kind = storedKind }))
            .Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;

        var response = await PrivatePasteFactory.PostClipAsync(c, meta with { Kind = uploadKind });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var again = (await response.Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
        Assert.Equal(first.Id, again.Id);
        Assert.True(again.Seq > first.Seq);
        Assert.Equal(first.Kind, again.Kind);
        Assert.Equal(storedKind == "link" ? ClipKind.Link : ClipKind.Text, again.Kind);
        Assert.Single((await c.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options))!.Items);
    }

    [Fact]
    public async Task Bump_that_brings_a_thumb_records_it()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var (meta, blob, thumb) = await PrivatePasteFactory.SealImageAsync(key, [1, 2, 3, 4]);
        var first = (await (await PrivatePasteFactory.PostClipAsync(c, meta, null, blob))
            .Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;
        Assert.False(first.HasThumb);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/clips/{first.Id}/thumb")).StatusCode);

        var again = (await (await PrivatePasteFactory.PostClipAsync(c, meta, thumb, blob))
            .Content.ReadFromJsonAsync<ClipDto>(ProtocolJson.Options))!;

        Assert.Equal(first.Id, again.Id);
        Assert.True(again.HasThumb);
        Assert.Equal(thumb, await c.GetByteArrayAsync($"/api/clips/{first.Id}/thumb"));
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/clips/{first.Id}")).StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.DataDir, "blobs")));
    }

    [Fact]
    public async Task Text_with_a_blob_or_thumb_is_400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var meta = PrivatePasteFactory.TextMeta(key, "plain");

        Assert.Equal(HttpStatusCode.BadRequest, (await PrivatePasteFactory.PostClipAsync(c, meta, null, [1, 2, 3])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PrivatePasteFactory.PostClipAsync(c, meta, [1, 2, 3])).StatusCode);
    }

    [Fact]
    public async Task Image_needs_exactly_one_blob()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var (meta, blob, thumb) = await PrivatePasteFactory.SealImageAsync(key, [1, 2, 3, 4]);

        Assert.Equal(HttpStatusCode.BadRequest, (await PrivatePasteFactory.PostClipAsync(c, meta, thumb)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PrivatePasteFactory.PostClipAsync(c, meta, thumb, blob, blob)).StatusCode);
    }

    [Fact]
    public async Task Image_parts_over_their_limits_are_413()
    {
        using var f = new PrivatePasteFactory(new Dictionary<string, string> { ["PP_MAX_IMAGE_MB"] = "1" });
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var (meta, blob, thumb) = await PrivatePasteFactory.SealImageAsync(key, [1, 2, 3, 4]);

        // A 1 MiB image seals to 1 MiB plus the 9-byte header and one 16-byte tag.
        const int maxBlob = 1024 * 1024 + 9 + 16;
        Assert.Equal(HttpStatusCode.OK, (await PrivatePasteFactory.PostClipAsync(c, meta, thumb, new byte[maxBlob])).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await PrivatePasteFactory.PostClipAsync(c, meta, thumb, new byte[maxBlob + 1])).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await PrivatePasteFactory.PostClipAsync(c, meta, new byte[1024 * 1024 + 1], blob)).StatusCode);
        var bigPayload = meta with { Payload = Convert.ToBase64String(new byte[4097]) };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await PrivatePasteFactory.PostClipAsync(c, bigPayload, thumb, blob)).StatusCode);
    }

    [Fact]
    public async Task SameHash_BumpsInsteadOfDuplicating()
    {
        using var f = new PrivatePasteFactory();
        var pc = await f.LoginAsync("PC", "windows");
        var mac = await f.LoginAsync("Mac", "macos");
        using var key = await PrivatePasteFactory.SetUpVaultAsync(pc);

        var first = await f.UploadTextAsync(pc, key, "same");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = await f.UploadTextAsync(mac, key, "same");

        Assert.Equal(first.Id, again.Id);
        Assert.True(again.Seq > first.Seq);
        Assert.Equal("macos", again.SourceDevicePlatform);
        Assert.Equal(f.Clock.GetUtcNow(), again.LastCopiedAt);
        Assert.Single((await pc.GetFromJsonAsync<ClipPage>("/api/clips", ProtocolJson.Options))!.Items);
    }

    [Fact]
    public async Task SearchQuery_Is400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();

        var response = await c.GetAsync("/api/clips?q=compose");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Search runs on your devices", (await ProblemAsync(response)).Detail);
    }

    [Fact]
    public async Task OldEndpoints_Are404Or405()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var (clip, _, _) = await f.UploadImageAsync(c, key, [1, 2, 3, 4]);

        HttpResponseMessage[] responses =
        [
            await c.PostAsJsonAsync("/api/clips/text", new TextClipUpload("old", null, false), ProtocolJson.Options),
            await c.PostAsync("/api/clips/image", new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "x.png" } }),
            await c.GetAsync($"/api/clips/{clip.Id}/image"),
            await c.GetAsync($"/api/clips/{clip.Id}/thumbnail"),
        ];

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"{r.RequestMessage!.RequestUri}: {r.StatusCode}"));
    }

    [Fact]
    public async Task Missing_meta_part_is_400()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);

        var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "blob0", "blob0" } };

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/clips", form)).StatusCode);
    }

    [Fact]
    public async Task Upload_does_not_buffer_to_temp_files()
    {
        using var f = new PrivatePasteFactory();
        var c = await f.LoginAsync();
        using var key = await PrivatePasteFactory.SetUpVaultAsync(c);
        var image = new byte[200 * 1024];
        Random.Shared.NextBytes(image);
        // Buffered form files are deleted when the request ends, so watch for their creation instead.
        var created = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var watcher = new FileSystemWatcher(Path.GetTempPath(), "ASPNETCORE_*") { EnableRaisingEvents = true };
        watcher.Created += (_, e) => created.Add(e.FullPath);

        await f.UploadImageAsync(c, key, image);
        await Task.Delay(300);

        Assert.Empty(created);
    }
}
