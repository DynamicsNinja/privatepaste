using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Images;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Client.Core.Sync;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;
using PrivatePaste.Tests.Images;
using SkiaSharp;

namespace PrivatePaste.Tests.Client;

public sealed class ClipCodecTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ServerLimitsDto Limits = ClipboardCoordinator.DefaultLimits;
    private readonly VaultKey _key = VaultKey.Create();

    public void Dispose() => _key.Dispose();

    /// <summary>The clip a server stores for an upload: everything it can see, nothing it can't.</summary>
    private static ClipDto Stored(ClipUpload upload, long seq = 1) =>
        new(Guid.NewGuid(), seq, upload.Meta.Kind switch { "text" => ClipKind.Text, "link" => ClipKind.Link, _ => ClipKind.Image },
            null, upload.Meta.Hash, null, null, upload.Meta.SizeBytes, Guid.NewGuid(), "B", T0)
        {
            Payload = upload.Meta.Payload,
            KeyId = upload.Meta.KeyId,
            HasThumb = upload.Thumb is not null,
            BlobCount = upload.Blobs.Count,
        };

    [Fact]
    public async Task Codec_TextRoundTripThroughFakeServer()
    {
        var codec = new ClipCodec(_key);
        var upload = await codec.EncodeAsync(new ClipboardContent.Text("hello"), T0, backfill: true, Limits, default);

        Assert.Equal("text", upload.Meta.Kind);
        Assert.Equal(_key.KeyId, upload.Meta.KeyId);
        Assert.Equal(KeyedHash.For(_key, ContentHash.ForText("hello")), upload.Meta.Hash);
        Assert.Equal(5, upload.Meta.SizeBytes);
        Assert.Equal(T0, upload.Meta.CopiedAt);
        Assert.True(upload.Meta.Backfill);
        Assert.Null(upload.Thumb);
        Assert.Empty(upload.Blobs);
        Assert.DoesNotContain("hello", Encoding.UTF8.GetString(Convert.FromBase64String(upload.Meta.Payload)));

        var clip = codec.Decode(Stored(upload));
        Assert.Equal("hello", clip.Text);
        Assert.Equal(ClipKind.Text, clip.Kind);
    }

    [Fact]
    public async Task Codec_TextIsNormalizedAndLinksAreClassified()
    {
        var codec = new ClipCodec(_key);
        var text = await codec.EncodeAsync(new ClipboardContent.Text("a\r\nb"), null, false, Limits, default);
        Assert.Equal("a\nb", codec.Decode(Stored(text)).Text);
        Assert.Equal(KeyedHash.For(_key, ContentHash.ForText("a\r\nb")), text.Meta.Hash);   // the same content hash every client computes
        Assert.Equal(3, text.Meta.SizeBytes);

        var link = await codec.EncodeAsync(new ClipboardContent.Text("https://example.com/x"), null, false, Limits, default);
        Assert.Equal("link", link.Meta.Kind);
        Assert.Equal(ClipKind.Link, codec.Decode(Stored(link)).Kind);
    }

    [Fact]
    public async Task Codec_ImageAppliesExifOrientationAndThumbFits256()
    {
        // 3 x 2 blocks of 200 px (600 x 400 stored), tagged "rotate 90 clockwise to display".
        var colors = new SKColor[3, 2];
        colors[0, 0] = SKColors.Red;
        colors[1, 0] = SKColors.Lime;
        colors[2, 0] = SKColors.Blue;
        colors[0, 1] = SKColors.Yellow;
        colors[1, 1] = SKColors.Magenta;
        colors[2, 1] = SKColors.Cyan;
        var jpeg = TestImages.MakeJpegWithOrientation(colors, 200, 6).ToArray();
        var codec = new ClipCodec(_key);

        var upload = await codec.EncodeAsync(new ClipboardContent.Image(jpeg), null, false, Limits, default);

        Assert.Equal("image", upload.Meta.Kind);
        var clip = codec.Decode(Stored(upload));
        Assert.Equal(ClipKind.Image, clip.Kind);
        Assert.Equal((400, 600), (clip.Width!.Value, clip.Height!.Value));   // swapped: upright
        var png = await codec.DecodeBlobAsync(Assert.Single(upload.Blobs), default);
        Assert.True(TestImages.IsPng(png));
        Assert.Equal((400, 600), TestImages.Dims(png));
        Assert.Equal(png.Length, upload.Meta.SizeBytes);
        Assert.Equal(KeyedHash.For(_key, ImageHashing.HashPng(png)), upload.Meta.Hash);
        // Rotated clockwise, the stored top-left block (red) is now top-right, and the stored bottom-left (yellow) top-left.
        using (var upright = SKBitmap.Decode(png))
        {
            AssertNear(SKColors.Red, upright.GetPixel(300, 100));
            AssertNear(SKColors.Yellow, upright.GetPixel(100, 100));
            AssertNear(SKColors.Cyan, upright.GetPixel(100, 500));
        }

        var thumb = codec.DecodeThumb(upload.Thumb!);
        Assert.True(TestImages.IsPng(thumb));
        var (w, h) = TestImages.Dims(thumb);
        Assert.True(w <= 256 && h <= 256, $"{w}x{h}");
        Assert.Equal(256, Math.Max(w, h));
        Assert.Equal((171, 256), (w, h));
    }

    private static void AssertNear(SKColor expected, SKColor actual)
    {
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 24);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 24);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 24);
    }

    [Fact]
    public async Task Codec_SmallImageKeepsItsSizeAsTheThumbnail()
    {
        var codec = new ClipCodec(_key);
        var upload = await codec.EncodeAsync(new ClipboardContent.Image(TestImages.MakePng(40, 30, 3).ToArray()), null, false, Limits, default);
        Assert.Equal((40, 30), TestImages.Dims(codec.DecodeThumb(upload.Thumb!)));
    }

    [Fact]
    public async Task Codec_Rejects51Megapixels()
    {
        // The header claims 7200 x 7200 (51.8 MP): rejected before any pixel is decoded.
        var png = TestImages.MakePngHeaderOnly(7200, 7200).ToArray();
        var codec = new ClipCodec(_key);
        var e = await Assert.ThrowsAsync<ClipTooLargeException>(() =>
            codec.EncodeAsync(new ClipboardContent.Image(png), null, false, Limits, default));
        Assert.Equal("Images over 50 megapixels aren't synced.", e.Message);
        Assert.True(ImagePreparation.IsOverPixelLimit(png));
        Assert.False(ImagePreparation.IsOverPixelLimit(TestImages.MakePngHeaderOnly(7000, 7000).ToArray()));
    }

    [Fact]
    public async Task Codec_RejectsImagesAndTextOverTheServersLimits()
    {
        var codec = new ClipCodec(_key);
        var noisy = TestImages.MakePng(800, 800, 9).ToArray();   // random pixels: about 1.9 MB as PNG
        var image = await Assert.ThrowsAsync<ClipTooLargeException>(() =>
            codec.EncodeAsync(new ClipboardContent.Image(noisy), null, false, new ServerLimitsDto(1024, 1), default));
        Assert.Equal("Images over 1 MB aren't synced.", image.Message);

        var text = await Assert.ThrowsAsync<ClipTooLargeException>(() =>
            codec.EncodeAsync(new ClipboardContent.Text(new string('x', 1025)), null, false, new ServerLimitsDto(1, 20), default));
        Assert.Equal("Clip too large to sync (text over 1 KB).", text.Message);
        // At the limit is fine, counted in UTF-8 bytes after normalizing: 512 two-byte letters are 1024 bytes.
        await codec.EncodeAsync(new ClipboardContent.Text(new string('é', 512)), null, false, new ServerLimitsDto(1, 20), default);
        await Assert.ThrowsAsync<ClipTooLargeException>(() =>
            codec.EncodeAsync(new ClipboardContent.Text(new string('é', 513)), null, false, new ServerLimitsDto(1, 20), default));
        Assert.Equal("Clip too large to sync (text over 1 MB).", ClipCodec.TextTooLargeMessage(1024));
    }

    [Fact]
    public async Task Codec_UnknownKindOrForeignKeyIdDecodesAsUnknown()
    {
        var codec = new ClipCodec(_key);
        var good = Stored(await codec.EncodeAsync(new ClipboardContent.Text("secret"), null, false, Limits, default));

        var newerKind = codec.Decode(good with { Kind = ClipKind.Unknown });
        Assert.Equal(ClipKind.Unknown, newerKind.Kind);
        Assert.Null(newerKind.Text);

        using var other = VaultKey.Create();
        var foreign = codec.Decode(Stored(await new ClipCodec(other).EncodeAsync(new ClipboardContent.Text("theirs"), null, false, Limits, default))
            with { Text = "server text", Width = 3, Height = 4 });
        Assert.Equal(ClipKind.Unknown, foreign.Kind);
        Assert.Equal((null, null, null), (foreign.Text, foreign.Width, foreign.Height));   // never the server's own fields

        // A payload under the right key id that doesn't open, isn't base64, or is missing: never throws.
        foreach (var bad in new[] { Convert.ToBase64String(new byte[40]), "not base64!", null })
        {
            var clip = codec.Decode(good with { Payload = bad });
            Assert.Equal(ClipKind.Unknown, clip.Kind);
            Assert.Null(clip.Text);
        }
        // Sealed, but not JSON.
        var notJson = Convert.ToBase64String(SealedBox.Seal(_key, "payload", "nope"u8));
        Assert.Equal(ClipKind.Unknown, codec.Decode(good with { Payload = notJson }).Kind);
        // A text clip whose payload has no text.
        var empty = ClipPayload.Seal(_key, new ClipPayload { Width = 1 });
        Assert.Equal(ClipKind.Unknown, codec.Decode(good with { Payload = empty }).Kind);
    }

    [Fact]
    public async Task Codec_KeyDisposedFailsCleanlyEverywhere()
    {
        var key = VaultKey.Create();
        var codec = new ClipCodec(key);
        var text = await codec.EncodeAsync(new ClipboardContent.Text("x"), null, false, Limits, default);
        var image = await codec.EncodeAsync(new ClipboardContent.Image(TestImages.MakePng(4, 4).ToArray()), null, false, Limits, default);
        key.Dispose();

        await Assert.ThrowsAsync<VaultKeyClosedException>(() =>
            codec.EncodeAsync(new ClipboardContent.Text("y"), null, false, Limits, default));
        await Assert.ThrowsAsync<VaultKeyClosedException>(() =>
            codec.EncodeAsync(new ClipboardContent.Image(TestImages.MakePng(4, 4).ToArray()), null, false, Limits, default));
        Assert.Throws<VaultKeyClosedException>(() => codec.Decode(Stored(text)));
        Assert.Throws<VaultKeyClosedException>(() => codec.DecodeThumb(image.Thumb!));
        await Assert.ThrowsAsync<VaultKeyClosedException>(() => codec.DecodeBlobAsync(image.Blobs[0], default));
        Assert.Equal(key.KeyId, codec.KeyId);   // still known, to tell which key was refused
    }

    [Fact]
    public async Task A_dispose_racing_seals_never_seals_with_wiped_bytes()
    {
        // Every seal either fails with ObjectDisposedException or used the real key: a box sealed with zeroed subkeys
        // would not open under a copy of the key.
        var raw = _key.Export();
        using var copy = VaultKey.FromBytes(raw);
        CryptographicOperations.ZeroMemory(raw);
        var sealedBoxes = new ConcurrentBag<byte[]>();
        var hashes = new ConcurrentBag<string>();
        var refused = 0;
        using var go = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            go.Wait();
            while (true)   // until the dispose stops this worker
            {
                try
                {
                    sealedBoxes.Add(SealedBox.Seal(_key, "payload", "plaintext"u8));
                    hashes.Add(KeyedHash.For(_key, "content"));
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.Increment(ref refused);
                    return;
                }
            }
        })).ToArray();
        go.Set();
        await Task.Delay(5);
        _key.Dispose();
        await Task.WhenAll(workers);

        Assert.Equal(4, Volatile.Read(ref refused));
        Assert.NotEmpty(sealedBoxes);
        foreach (var box in sealedBoxes) Assert.Equal("plaintext"u8.ToArray(), SealedBox.Open(copy, "payload", box));
        Assert.All(hashes, h => Assert.Equal(KeyedHash.For(copy, "content"), h));
    }
}
