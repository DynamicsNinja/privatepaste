using SkiaSharp;

namespace PrivatePaste.Tests.Images;

internal static class TestImages
{
    public static MemoryStream MakePng(int w, int h, int seed = 1) => Encode(w, h, seed, SKEncodedImageFormat.Png);
    public static MemoryStream MakeJpeg(int w, int h, int seed = 1) => Encode(w, h, seed, SKEncodedImageFormat.Jpeg);
    public static MemoryStream MakeWebpLossless(int w, int h, int seed = 1) => Encode(w, h, seed, SKEncodedImageFormat.Webp);

    private static MemoryStream Encode(int w, int h, int seed, SKEncodedImageFormat format)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var rnd = new Random(seed);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255));
        using var data = (format == SKEncodedImageFormat.Webp
            ? bmp.PeekPixels().Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            : bmp.Encode(format, 95))!;
        return new MemoryStream(data.ToArray());
    }

    public static (int W, int H) Dims(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        return (bmp.Width, bmp.Height);
    }

    public static bool IsPng(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
}
