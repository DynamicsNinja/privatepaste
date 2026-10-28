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

    /// <summary>A JPEG of <paramref name="blocks"/> (cols x rows, each block <paramref name="cell"/> px square) tagged with an EXIF orientation.</summary>
    public static MemoryStream MakeJpegWithOrientation(SKColor[,] blocks, int cell, int orientation)
    {
        var cols = blocks.GetLength(0);
        var rows = blocks.GetLength(1);
        using var bmp = new SKBitmap(new SKImageInfo(cols * cell, rows * cell, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                bmp.SetPixel(x, y, blocks[x / cell, y / cell]);
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 100)!;
        var jpeg = data.ToArray();

        // APP1 "Exif" segment holding a single IFD0 Orientation entry, inserted right after SOI.
        byte[] payload =
        [
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,       // little-endian TIFF header, IFD0 at offset 8
            1, 0,                                           // one entry
            0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, // tag 0x0112, SHORT, count 1
            0, 0, 0, 0,                                     // no next IFD
        ];
        var len = payload.Length + 2;
        var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);
        ms.Write([0xFF, 0xE1, (byte)(len >> 8), (byte)len]);
        ms.Write(payload);
        ms.Write(jpeg, 2, jpeg.Length - 2);
        ms.Position = 0;
        return ms;
    }

    /// <summary>A PNG whose IHDR claims the given size; it carries no pixel data and cannot be decoded.</summary>
    public static MemoryStream MakePngHeaderOnly(int w, int h)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(ms, "IHDR"u8, ihdr);
        WriteChunk(ms, "IDAT"u8, [0x78, 0x9C, 0x00]); // truncated zlib stream: header parses, pixels never decode
        WriteChunk(ms, "IEND"u8, []);
        ms.Position = 0;
        return ms;
    }

    private static void WriteChunk(Stream ms, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        var chunk = new byte[4 + body.Length];
        type.CopyTo(chunk);
        body.CopyTo(chunk.AsSpan(4));
        var num = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(num, body.Length);
        ms.Write(num);
        ms.Write(chunk);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(num, Crc32(chunk));
        ms.Write(num);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public static (int W, int H) Dims(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        return (bmp.Width, bmp.Height);
    }

    public static bool IsPng(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
}
