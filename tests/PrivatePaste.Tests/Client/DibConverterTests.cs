using System.Buffers.Binary;
using PrivatePaste.Desktop.Platform.Windows;
using SkiaSharp;

namespace PrivatePaste.Tests.Client;

public class DibConverterTests
{
    // 3x2, distinct opaque colors; (0,0) top-left.
    private static readonly SKColor[,] Pixels =
    {
        { new(255, 0, 0), new(0, 255, 0), new(0, 0, 255) },
        { new(10, 20, 30), new(200, 100, 50), new(255, 255, 255) },
    };

    private static byte[] MakePng(byte alpha = 255)
    {
        using var bmp = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 3; x++)
                bmp.SetPixel(x, y, Pixels[y, x].WithAlpha(alpha));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKColor[,] Decode(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        var r = new SKColor[bmp.Height, bmp.Width];
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                r[y, x] = bmp.GetPixel(x, y);
        return r;
    }

    private static void AssertPixels(byte[] png, byte alpha = 255, int tolerance = 0)
    {
        var d = Decode(png);
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 3; x++)
            {
                var e = Pixels[y, x];
                Assert.Equal(alpha, d[y, x].Alpha);
                Assert.InRange(d[y, x].Red, e.Red - tolerance, e.Red + tolerance);
                Assert.InRange(d[y, x].Green, e.Green - tolerance, e.Green + tolerance);
                Assert.InRange(d[y, x].Blue, e.Blue - tolerance, e.Blue + tolerance);
            }
    }

    [Fact]
    public void Dib_roundtrip_preserves_pixels()
    {
        var dib = DibConverter.FromPng(MakePng());
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(dib));
        AssertPixels(DibConverter.ToPng(dib));
    }

    [Fact]
    public void Dib_roundtrip_preserves_alpha()
    {
        AssertPixels(DibConverter.ToPng(DibConverter.FromPng(MakePng(128))), 128, tolerance: 2);   // Skia premultiplies in the test's own source bitmap
    }

    private static byte[] BuildDib(int bitCount, int compression, bool topDown, uint[]? masks = null, bool v5 = false)
    {
        var bpp = bitCount / 8;
        var stride = (3 * bitCount + 31) / 32 * 4;
        var headerSize = v5 ? 124 : 40;
        var maskBytes = !v5 && masks is not null ? 12 : 0;
        var dib = new byte[headerSize + maskBytes + stride * 2];
        BinaryPrimitives.WriteInt32LittleEndian(dib, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), topDown ? -2 : 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), (ushort)bitCount);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(16), compression);
        if (masks is not null)
        {
            var at = v5 ? 40 : headerSize;
            for (var i = 0; i < 3; i++) BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(at + i * 4), masks[i]);
        }
        var pixelStart = headerSize + maskBytes;
        for (var y = 0; y < 2; y++)
        {
            var row = pixelStart + stride * (topDown ? y : 1 - y);
            for (var x = 0; x < 3; x++)
            {
                var c = Pixels[y, x];
                if (compression == 3)   // 0x00RRGGBB (masks as given)
                {
                    var v = ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
                    BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(row + x * 4), v);
                }
                else
                {
                    dib[row + x * bpp] = c.Blue; dib[row + x * bpp + 1] = c.Green; dib[row + x * bpp + 2] = c.Red;
                    if (bpp == 4) dib[row + x * bpp + 3] = 0;   // unused alpha byte
                }
            }
        }
        return dib;
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(24, true)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void Bottom_up_and_top_down_dib_both_decode(int bits, bool topDown)
    {
        AssertPixels(DibConverter.ToPng(BuildDib(bits, 0, topDown)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bitfields_dib_decodes(bool v5)
    {
        var dib = BuildDib(32, 3, topDown: false, masks: [0x00FF0000, 0x0000FF00, 0x000000FF], v5: v5);
        AssertPixels(DibConverter.ToPng(dib));
    }

    [Fact]
    public void Truncated_or_garbage_dib_throws_InvalidData()
    {
        var dib = BuildDib(24, 0, false);
        Assert.Throws<InvalidDataException>(() => DibConverter.ToPng(dib[..60]));
        Assert.Throws<InvalidDataException>(() => DibConverter.ToPng([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => DibConverter.FromPng([1, 2, 3]));
    }
}
