using PrivatePaste.Protocol;
using SkiaSharp;

namespace PrivatePaste.Server.Images;

public sealed class InvalidImageException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ImageTooLargeException(string message) : Exception(message);

public sealed record ProcessedImage(byte[] Png, byte[] ThumbnailPng, int Width, int Height, string Hash);

public sealed class ImageProcessor
{
    private const int ThumbnailMax = 256;
    private const long MaxPixels = 50_000_000;

    public ProcessedImage Process(Stream input)
    {
        using var buffered = new MemoryStream();
        input.CopyTo(buffered);
        buffered.Position = 0;

        using var codec = SKCodec.Create(buffered);
        if (codec is null) throw new InvalidImageException("Input is not a decodable image.");
        var size = codec.Info;
        if (size.Width <= 0 || size.Height <= 0) throw new InvalidImageException("Image has no pixels.");

        if (size.Width * (long)size.Height > MaxPixels)
            throw new ImageTooLargeException("Image too large (over 50 megapixels).");

        var decodeInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var decoded = SKBitmap.Decode(codec, decodeInfo);
        if (decoded is null) throw new InvalidImageException("Image could not be decoded.");
        if (decoded.GetPixelSpan().Length != decodeInfo.Width * decodeInfo.Height * 4)
            throw new InvalidImageException("Decoded pixel buffer has an unexpected size.");

        using var bitmap = ApplyOrigin(decoded, codec.EncodedOrigin);
        var info = bitmap.Info;
        var pixels = bitmap.GetPixelSpan();
        var hash = ContentHash.ForPixels(pixels, info.Width, info.Height);

        var png = EncodePng(bitmap);

        byte[] thumb;
        if (info.Width <= ThumbnailMax && info.Height <= ThumbnailMax)
        {
            thumb = png;
        }
        else
        {
            var scale = Math.Min((double)ThumbnailMax / info.Width, (double)ThumbnailMax / info.Height);
            var tw = Math.Max(1, (int)Math.Round(info.Width * scale));
            var th = Math.Max(1, (int)Math.Round(info.Height * scale));
            using var small = bitmap.Resize(info.WithSize(tw, th), new SKSamplingOptions(SKCubicResampler.Mitchell))
                ?? throw new InvalidImageException("Thumbnail could not be created.");
            thumb = EncodePng(small);
        }

        return new ProcessedImage(png, thumb, info.Width, info.Height, hash);
    }

    /// <summary>Returns the pixels rotated/flipped upright (a lossless copy); the source when already upright.</summary>
    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return src.Copy();

        var w = src.Width;
        var h = src.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dw = swap ? h : w;
        var dh = swap ? w : h;
        var dst = new SKBitmap(new SKImageInfo(dw, dh, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var s = src.GetPixelSpan();
        var d = dst.GetPixelSpan();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (nx, ny) = origin switch
                {
                    SKEncodedOrigin.TopRight => (w - 1 - x, y),
                    SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),
                    SKEncodedOrigin.BottomLeft => (x, h - 1 - y),
                    SKEncodedOrigin.LeftTop => (y, x),
                    SKEncodedOrigin.RightTop => (h - 1 - y, x),
                    SKEncodedOrigin.RightBottom => (h - 1 - y, w - 1 - x),
                    SKEncodedOrigin.LeftBottom => (y, w - 1 - x),
                    _ => (x, y),
                };
                s.Slice((y * w + x) * 4, 4).CopyTo(d.Slice((ny * dw + nx) * 4, 4));
            }
        }
        return dst;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidImageException("PNG encoding failed.");
        return data.ToArray();
    }
}
