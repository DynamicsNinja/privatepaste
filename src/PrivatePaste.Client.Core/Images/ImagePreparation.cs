using System.Runtime.InteropServices;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;
using SkiaSharp;

namespace PrivatePaste.Client.Core.Images;

/// <summary>
/// An image ready to seal: the upright PNG, its size, a PNG thumbnail fitting 256x256, and the
/// <see cref="Protocol.ContentHash.ForPixels"/> hash of the upright pixels.
/// </summary>
public sealed record PreparedImage(byte[] Png, int Width, int Height, byte[] ThumbPng, string ContentHash);

/// <summary>
/// Turns any image SkiaSharp can decode into what every client stores: an upright PNG (EXIF orientation applied, since
/// the server can no longer do it) and a small PNG thumbnail. Same rules and copy as Android's ImagePreparation.
/// Decodes the whole image: call it off the UI thread.
/// </summary>
public static class ImagePreparation
{
    public const long MaxPixels = 50_000_000;
    public const int ThumbSize = 256;
    public const string TooManyPixels = "Images over 50 megapixels aren't synced.";

    public static string TooLargeMessage(long imageMb) => $"Images over {imageMb} MB aren't synced.";

    /// <summary>True when the image's header says it is over <see cref="MaxPixels"/>; reads the header only.</summary>
    public static bool IsOverPixelLimit(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded));
        return codec is not null && (long)codec.Info.Width * codec.Info.Height > MaxPixels;
    }

    /// <exception cref="ClipTooLargeException">Over <see cref="MaxPixels"/>, or the PNG is over <paramref name="maxBytes"/>.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
    public static PreparedImage Prepare(byte[] encoded, long maxBytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded)) ?? throw new InvalidDataException("Not a decodable image.");
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0) throw new InvalidDataException("Image has no pixels.");
        // Checked before decoding: a huge image would not fit in memory anyway.
        if ((long)width * height > MaxPixels) throw new ClipTooLargeException(TooManyPixels);

        SKBitmap decoded;
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            decoded = SKBitmap.Decode(codec, info) ?? throw new InvalidDataException("Image could not be decoded.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception e) { throw new InvalidDataException("Image could not be decoded.", e); }

        var upright = decoded;
        try
        {
            if (decoded.GetPixelSpan().Length != width * height * 4) throw new InvalidDataException("Unexpected pixel buffer size.");
            upright = Orient(decoded, codec.EncodedOrigin);
            // Two full-size bitmaps at once is the peak: free the unrotated one as soon as the upright copy exists.
            if (!ReferenceEquals(upright, decoded)) decoded.Dispose();
            var (w, h) = (upright.Width, upright.Height);
            var png = EncodePng(upright);
            if (png.Length > maxBytes) throw new ClipTooLargeException(TooLargeMessage(maxBytes / (1024 * 1024)));
            var thumb = Math.Max(w, h) <= ThumbSize ? png : Thumbnail(upright);
            return new PreparedImage(png, w, h, thumb, Protocol.ContentHash.ForPixels(upright.GetPixelSpan(), w, h));
        }
        finally
        {
            upright.Dispose();
            decoded.Dispose();
        }
    }

    /// <summary>
    /// The image turned upright: an exact remap of the pixels (no resampling), so every client hashes the same pixels.
    /// Returns <paramref name="src"/> itself when it already is.
    /// </summary>
    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft || (int)origin is < 1 or > 8) return src;
        var (w, h) = (src.Width, src.Height);
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dw = swaps ? h : w;
        var dst = new SKBitmap(new SKImageInfo(dw, swaps ? w : h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var from = MemoryMarshal.Cast<byte, uint>(src.GetPixelSpan());
        var to = MemoryMarshal.Cast<byte, uint>(dst.GetPixelSpan());
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (dx, dy) = origin switch
                {
                    SKEncodedOrigin.TopRight => (w - 1 - x, y),             // flip horizontal
                    SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),  // rotate 180
                    SKEncodedOrigin.BottomLeft => (x, h - 1 - y),           // flip vertical
                    SKEncodedOrigin.LeftTop => (y, x),                      // transpose
                    SKEncodedOrigin.RightTop => (h - 1 - y, x),             // rotate 90 clockwise
                    SKEncodedOrigin.RightBottom => (h - 1 - y, w - 1 - x),  // transverse
                    _ => (y, w - 1 - x),                                    // LeftBottom: rotate 270 clockwise
                };
                to[dy * dw + dx] = from[y * w + x];
            }
        return dst;
    }

    /// <summary>Scaled down to fit <see cref="ThumbSize"/> on both sides.</summary>
    private static byte[] Thumbnail(SKBitmap bitmap)
    {
        var scale = (double)ThumbSize / Math.Max(bitmap.Width, bitmap.Height);
        var w = Math.Clamp((int)Math.Round(bitmap.Width * scale), 1, ThumbSize);
        var h = Math.Clamp((int)Math.Round(bitmap.Height * scale), 1, ThumbSize);
        using var thumb = bitmap.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidDataException("The thumbnail could not be made.");
        return EncodePng(thumb);
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("PNG encoding failed.");
        return data.ToArray();
    }
}
