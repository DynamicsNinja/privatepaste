using PrivatePaste.Protocol;
using PrivatePaste.Server.Images;
using SkiaSharp;
using static PrivatePaste.Tests.Images.TestImages;

namespace PrivatePaste.Tests.Images;

public class ImageProcessorTests
{
    private readonly ImageProcessor proc = new();

    [Fact]
    public void Jpeg_becomes_png_with_dims()
    {
        var r = proc.Process(MakeJpeg(1280, 720));
        Assert.Equal((1280, 720), (r.Width, r.Height));
        Assert.True(IsPng(r.Png));
        Assert.Equal((1280, 720), Dims(r.Png));
    }

    [Fact]
    public void Thumbnail_fits_256_preserving_aspect()
    {
        var r = proc.Process(MakePng(1280, 720));
        Assert.Equal((256, 144), Dims(r.ThumbnailPng));
    }

    [Fact]
    public void Small_image_not_upscaled()
    {
        var r = proc.Process(MakePng(100, 50));
        Assert.Equal((100, 50), Dims(r.ThumbnailPng));
    }

    [Fact]
    public void Same_pixels_different_encoding_same_hash() =>
        Assert.Equal(proc.Process(MakePng(10, 10, seed: 1)).Hash, proc.Process(MakeWebpLossless(10, 10, seed: 1)).Hash);

    [Fact]
    public void Different_pixels_different_hash() =>
        Assert.NotEqual(proc.Process(MakePng(10, 10, seed: 1)).Hash, proc.Process(MakePng(10, 10, seed: 2)).Hash);

    [Fact]
    public void Garbage_throws() =>
        Assert.Throws<InvalidImageException>(() => proc.Process(new MemoryStream("not an image"u8.ToArray())));

    private static readonly SKColor A = new(255, 0, 0), B = new(0, 255, 0), C = new(0, 0, 255),
        D = new(255, 255, 0), E = new(0, 255, 255), F = new(255, 0, 255);

    // Source layout (3 cols x 2 rows):  A B C / D E F
    private static SKColor[,] Blocks() => new[,] { { A, D }, { B, E }, { C, F } };

    private static SKColor TopLeft(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        return bmp.GetPixel(4, 4);
    }

    private static bool Near(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) < 40 && Math.Abs(a.Green - b.Green) < 40 && Math.Abs(a.Blue - b.Blue) < 40;

    [Fact]
    public void Exif_rotated_jpeg_is_normalized_upright()
    {
        // 2x1 source "red | blue", orientation 6 (rotate 90 CW) => 1x2 upright, red on top.
        var r = proc.Process(MakeJpegWithOrientation(new[,] { { A }, { C } }, 16, 6));
        Assert.Equal((16, 32), (r.Width, r.Height));
        Assert.Equal((16, 32), Dims(r.Png));

        using var bmp = SKBitmap.Decode(r.Png);
        var rgba = new SKBitmap(new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.True(bmp.CopyTo(rgba, SKColorType.Rgba8888));
        Assert.Equal(ContentHash.ForPixels(rgba.GetPixelSpan(), rgba.Width, rgba.Height), r.Hash);
        Assert.True(Near(bmp.GetPixel(8, 4), A));
        Assert.True(Near(bmp.GetPixel(8, 28), C));
    }

    [Theory]
    [InlineData(1, 'A', 48, 32)]
    [InlineData(2, 'C', 48, 32)]
    [InlineData(3, 'F', 48, 32)]
    [InlineData(4, 'D', 48, 32)]
    [InlineData(5, 'A', 32, 48)]
    [InlineData(6, 'D', 32, 48)]
    [InlineData(7, 'F', 32, 48)]
    [InlineData(8, 'C', 32, 48)]
    public void Every_exif_orientation_maps_to_upright(int orientation, char expected, int w, int h)
    {
        var want = expected switch { 'A' => A, 'C' => C, 'D' => D, _ => F };
        var r = proc.Process(MakeJpegWithOrientation(Blocks(), 16, orientation));
        Assert.Equal((w, h), (r.Width, r.Height));
        Assert.Equal((w, h), Dims(r.Png));
        Assert.True(Near(TopLeft(r.Png), want), $"origin {orientation}: got {TopLeft(r.Png)}");
    }

    [Fact]
    public void Image_over_50_megapixels_is_rejected_before_decoding() =>
        Assert.Throws<ImageTooLargeException>(() => proc.Process(MakePngHeaderOnly(10000, 5001)));
}
