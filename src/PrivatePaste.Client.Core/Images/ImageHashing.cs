using PrivatePaste.Protocol;
using SkiaSharp;

namespace PrivatePaste.Client.Core.Images;

public static class ImageHashing
{
    /// <summary>Decodes to Rgba8888/Unpremul (as the server does) and hashes with <see cref="ContentHash.ForPixels"/>.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
    public static string HashPng(byte[] png)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(png));
            if (codec is null) throw new InvalidDataException("Not a decodable image.");
            var size = codec.Info;
            if (size.Width <= 0 || size.Height <= 0) throw new InvalidDataException("Image has no pixels.");
            var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bitmap = SKBitmap.Decode(codec, info) ?? throw new InvalidDataException("Image could not be decoded.");
            var pixels = bitmap.GetPixelSpan();
            if (pixels.Length != info.Width * info.Height * 4) throw new InvalidDataException("Unexpected pixel buffer size.");
            return ContentHash.ForPixels(pixels, info.Width, info.Height);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception e) { throw new InvalidDataException("Image could not be decoded.", e); }
    }
}
