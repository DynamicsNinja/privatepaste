using PrivatePaste.Server.Images;
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
}
