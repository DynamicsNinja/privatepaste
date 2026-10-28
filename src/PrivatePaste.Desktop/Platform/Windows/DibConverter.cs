using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace PrivatePaste.Desktop.Platform.Windows;

/// <summary>
/// Pure byte conversion between PNG and the packed DIB (BITMAPINFOHEADER/V4/V5 + pixels) that the Windows clipboard uses.
/// Supports BI_RGB 24/32-bit and BI_BITFIELDS (16/32-bit), bottom-up and top-down. Not Windows-specific.
/// </summary>
public static class DibConverter
{
    private const int BiRgb = 0, BiBitfields = 3, BiAlphaBitfields = 6;
    private const int InfoHeaderSize = 40;

    /// <exception cref="InvalidDataException">The DIB is malformed or uses an unsupported format.</exception>
    public static byte[] ToPng(byte[] dib)
    {
        if (dib.Length < InfoHeaderSize) throw new InvalidDataException("DIB too small.");
        var span = dib.AsSpan();
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(span);
        if (headerSize < InfoHeaderSize || headerSize > dib.Length) throw new InvalidDataException("Unsupported DIB header.");
        var width = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
        var clrUsed = BinaryPrimitives.ReadUInt32LittleEndian(span[32..]);

        var topDown = rawHeight < 0;
        var height = Math.Abs((long)rawHeight);
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768) throw new InvalidDataException("Bad DIB dimensions.");
        if (compression is not (BiRgb or BiBitfields or BiAlphaBitfields)) throw new InvalidDataException("Unsupported DIB compression.");

        uint rMask, gMask, bMask, aMask = 0;
        var offset = (long)headerSize;
        if (compression == BiRgb)
        {
            if (bitCount is not (24 or 32)) throw new InvalidDataException("Unsupported DIB bit depth.");
            (rMask, gMask, bMask) = (0x00FF0000, 0x0000FF00, 0x000000FF);
        }
        else
        {
            if (bitCount is not (16 or 32)) throw new InvalidDataException("Unsupported DIB bit depth.");
            if (headerSize >= 108)   // V4/V5: masks live in the header
            {
                rMask = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                gMask = BinaryPrimitives.ReadUInt32LittleEndian(span[44..]);
                bMask = BinaryPrimitives.ReadUInt32LittleEndian(span[48..]);
                aMask = BinaryPrimitives.ReadUInt32LittleEndian(span[52..]);
            }
            else   // BITMAPINFOHEADER: 3 (or 4 with alpha) DWORD masks follow it
            {
                var maskBytes = compression == BiAlphaBitfields ? 16 : 12;
                if (dib.Length < headerSize + maskBytes) throw new InvalidDataException("DIB too small.");
                rMask = BinaryPrimitives.ReadUInt32LittleEndian(span[headerSize..]);
                gMask = BinaryPrimitives.ReadUInt32LittleEndian(span[(headerSize + 4)..]);
                bMask = BinaryPrimitives.ReadUInt32LittleEndian(span[(headerSize + 8)..]);
                if (compression == BiAlphaBitfields) aMask = BinaryPrimitives.ReadUInt32LittleEndian(span[(headerSize + 12)..]);
                offset += maskBytes;
            }
            if (rMask == 0 || gMask == 0 || bMask == 0) throw new InvalidDataException("Bad DIB color masks.");
        }
        offset += clrUsed * 4L;

        var bytesPerPixel = bitCount / 8;
        var stride = ((long)width * bitCount + 31) / 32 * 4;
        if (offset + stride * height > dib.Length) throw new InvalidDataException("DIB pixel data truncated.");

        var rgba = new byte[width * (int)height * 4];
        var anyAlpha = false;
        var dst = 0;
        for (var y = 0; y < height; y++)
        {
            var srcRow = (int)(offset + stride * (topDown ? y : height - 1 - y));
            for (var x = 0; x < width; x++)
            {
                var p = srcRow + x * bytesPerPixel;
                byte r, g, b, a = 255;
                if (compression == BiRgb)
                {
                    b = dib[p]; g = dib[p + 1]; r = dib[p + 2];
                    if (bitCount == 32) { a = dib[p + 3]; anyAlpha |= a != 0; }
                }
                else
                {
                    uint v = bitCount == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(span[p..]) : BinaryPrimitives.ReadUInt16LittleEndian(span[p..]);
                    r = Extract(v, rMask); g = Extract(v, gMask); b = Extract(v, bMask);
                    if (aMask != 0) { a = Extract(v, aMask); anyAlpha |= a != 0; }
                }
                rgba[dst++] = r; rgba[dst++] = g; rgba[dst++] = b; rgba[dst++] = a;
            }
        }
        // 32-bit BI_RGB very often carries an unused (zero) fourth byte: if every alpha is zero the image is opaque.
        if (!anyAlpha && (bitCount == 32 || aMask != 0))
            for (var i = 3; i < rgba.Length; i += 4) rgba[i] = 255;

        var info = new SKImageInfo(width, (int)height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("PNG encoding failed.");
        return data.ToArray();
    }

    /// <summary>Packed bottom-up 32-bit BI_RGB DIB (BITMAPINFOHEADER + BGRA). Alpha is kept.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
    public static byte[] FromPng(byte[] png)
    {
        using var codec = SKCodec.Create(new MemoryStream(png)) ?? throw new InvalidDataException("Not a decodable image.");
        var size = codec.Info;
        if (size.Width <= 0 || size.Height <= 0) throw new InvalidDataException("Image has no pixels.");
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = SKBitmap.Decode(codec, info) ?? throw new InvalidDataException("Image could not be decoded.");
        var pixels = bitmap.GetPixelSpan();
        var rowBytes = info.Width * 4;
        if (pixels.Length != rowBytes * info.Height) throw new InvalidDataException("Unexpected pixel buffer size.");

        var dib = new byte[InfoHeaderSize + pixels.Length];
        var h = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h, InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], info.Width);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], info.Height);   // positive: bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], BiRgb);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], pixels.Length);
        for (var y = 0; y < info.Height; y++)
            pixels.Slice(y * rowBytes, rowBytes).CopyTo(dib.AsSpan(InfoHeaderSize + (info.Height - 1 - y) * rowBytes));
        return dib;
    }

    /// <summary>Extracts the channel selected by <paramref name="mask"/>, scaled to 0..255.</summary>
    private static byte Extract(uint value, uint mask)
    {
        var shift = BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        var v = (value & mask) >> shift;
        return max == 255 ? (byte)v : (byte)(((ulong)v * 255 + max / 2) / max);
    }
}
