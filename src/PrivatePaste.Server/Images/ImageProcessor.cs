using PrivatePaste.Protocol;
using SkiaSharp;

namespace PrivatePaste.Server.Images;

public sealed class InvalidImageException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ProcessedImage(byte[] Png, byte[] ThumbnailPng, int Width, int Height, string Hash);

public sealed class ImageProcessor
{
    private const int ThumbnailMax = 256;

    public ProcessedImage Process(Stream input)
    {
        using var buffered = new MemoryStream();
        input.CopyTo(buffered);
        buffered.Position = 0;

        using var codec = SKCodec.Create(buffered);
        if (codec is null) throw new InvalidImageException("Input is not a decodable image.");
        var size = codec.Info;
        if (size.Width <= 0 || size.Height <= 0) throw new InvalidImageException("Image has no pixels.");

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = SKBitmap.Decode(codec, info);
        if (bitmap is null) throw new InvalidImageException("Image could not be decoded.");

        var pixels = bitmap.GetPixelSpan();
        if (pixels.Length != info.Width * info.Height * 4)
            throw new InvalidImageException("Decoded pixel buffer has an unexpected size.");
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

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidImageException("PNG encoding failed.");
        return data.ToArray();
    }
}
