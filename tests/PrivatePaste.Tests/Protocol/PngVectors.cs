using System.Buffers.Binary;
using System.Text;

namespace PrivatePaste.Tests.Protocol;

/// <summary>
/// Hand-built PNGs for the "pngHash" group of docs/e2e-test-vectors.json: the colour types, bit depths, filters and
/// interlacing a clipboard PNG may use, with semi-transparent pixels. The image data is zlib with stored blocks, so the
/// bytes never depend on a zlib version. Their hashes come from <c>ImageHashing.HashPng</c> (SkiaSharp, unpremultiplied
/// RGBA8888), which a client decoding PNGs itself (the browser extension) must reproduce exactly.
/// </summary>
public static class PngVectors
{
    public sealed record Case(string Name, int Width, int Height, byte[] Png);

    private const byte Gray = 0, Rgb = 2, Palette = 3, GrayAlpha = 4, Rgba = 6;

    /// <summary>The pixels of "rgba8-filters-alpha" as unpremultiplied RGBA8888: alpha 0, 1, 127, 128, 254 and 255 among them.</summary>
    public static byte[] SemiTransparentRgba { get; } = BuildSemiTransparent();

    public static IReadOnlyList<Case> Cases { get; } = BuildCases();

    private static byte[] BuildSemiTransparent()
    {
        byte[] alphas = [255, 128, 0, 1, 254, 127, 64, 200, 2];
        var px = new byte[4 * 5 * 4];
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4;
                px[i] = (byte)(x * 67 + y * 31);
                px[i + 1] = (byte)(x * 13 + y * 101 + 7);
                px[i + 2] = (byte)(x * 29 + y * 53 + 200);
                px[i + 3] = alphas[(x + y * 4) % alphas.Length];
            }
        return px;
    }

    private static List<Case> BuildCases()
    {
        var cases = new List<Case>();

        // One row per filter type (None, Sub, Up, Average, Paeth).
        cases.Add(new("rgba8-filters-alpha", 4, 5,
            Png(4, 5, 8, Rgba, Rows(SemiTransparentRgba, 4 * 4, 5), filters: [0, 1, 2, 3, 4])));

        // Adam7 interlacing, 8-bit RGB, filters cycling through the rows of each pass.
        var rgb = new byte[5 * 5 * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 37 + 11);
        cases.Add(new("rgb8-adam7", 5, 5, Png(5, 5, 8, Rgb, Rows(rgb, 5 * 3, 5), interlaced: true)));

        // 2-bit palette whose tRNS covers only the first three entries (the fourth stays opaque).
        byte[] plte = [255, 0, 0, 0, 255, 0, 0, 0, 255, 250, 250, 10];
        byte[] trns = [0, 128, 255];
        cases.Add(new("palette2-trns", 3, 2,
            Png(3, 2, 2, Palette, [Pack([0, 1, 2], 2), Pack([3, 2, 1], 2)], plte: plte, trns: trns, filters: [0, 1])));

        cases.Add(new("gray4", 3, 2, Png(3, 2, 4, Gray, [Pack([0, 7, 15], 4), Pack([1, 8, 14], 4)])));

        // tRNS for gray marks one sample value as fully transparent.
        cases.Add(new("gray8-trns", 3, 1, Png(3, 1, 8, Gray, [[0x10, 0x40, 0xf0]], trns: [0x00, 0x40])));

        cases.Add(new("graya8", 2, 2, Png(2, 2, 8, GrayAlpha, [[0x20, 0x80, 0xff, 0x01], [0x7f, 0x00, 0x00, 0xfe]], filters: [2, 4])));

        // 16-bit samples, including values whose low byte decides the rounding.
        ushort[] rgba16 = [0x1234, 0x80ff, 0x7f80, 0xffff, 0x00ff, 0x0180, 0xfe7f, 0x8080, 0xffff, 0x0000, 0x7fff, 0x0101, 0x00ff, 0xff7f, 0x807f, 0x7f7f];
        cases.Add(new("rgba16", 2, 2, Png(2, 2, 16, Rgba, Rows(BigEndian(rgba16), 2 * 8, 2), filters: [1, 3])));

        return cases;
    }

    private static byte[] BigEndian(ushort[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(i * 2), samples[i]);
        return b;
    }

    private static byte[][] Rows(byte[] data, int stride, int height) =>
        Enumerable.Range(0, height).Select(y => data.AsSpan(y * stride, stride).ToArray()).ToArray();

    /// <summary>Packs sub-byte samples MSB first, the row padded to a whole byte.</summary>
    private static byte[] Pack(int[] samples, int depth)
    {
        var row = new byte[(samples.Length * depth + 7) / 8];
        for (var i = 0; i < samples.Length; i++)
        {
            var bit = i * depth;
            row[bit / 8] |= (byte)(samples[i] << (8 - depth - bit % 8));
        }
        return row;
    }

    private static int Channels(byte colorType) => colorType switch
    {
        Gray or Palette => 1,
        GrayAlpha => 2,
        Rgb => 3,
        _ => 4,
    };

    private static byte[] Png(int width, int height, int depth, byte colorType, byte[][] rows, byte[]? plte = null,
        byte[]? trns = null, int[]? filters = null, bool interlaced = false)
    {
        var bitsPerPixel = depth * Channels(colorType);
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var raw = new MemoryStream();
        if (!interlaced)
        {
            Filter(raw, rows, bpp, filters ?? new int[rows.Length]);
        }
        else
        {
            // Byte-aligned pixels only (8-bit and up): a pass picks whole pixels out of each full row.
            (int X, int Y, int Dx, int Dy)[] passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
            foreach (var (x0, y0, dx, dy) in passes)
            {
                var passRows = new List<byte[]>();
                for (var y = y0; y < height; y += dy)
                {
                    var row = new List<byte>();
                    for (var x = x0; x < width; x += dx) row.AddRange(rows[y].AsSpan(x * bpp, bpp).ToArray());
                    if (row.Count > 0) passRows.Add([.. row]);
                }
                if (passRows.Count > 0) Filter(raw, [.. passRows], bpp, passRows.Select((_, i) => i % 5).ToArray());
            }
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = (byte)depth;
        ihdr[9] = colorType;
        ihdr[12] = (byte)(interlaced ? 1 : 0);
        Chunk(png, "IHDR", ihdr);
        if (plte is not null) Chunk(png, "PLTE", plte);
        if (trns is not null) Chunk(png, "tRNS", trns);
        Chunk(png, "IDAT", ZlibStored(raw.ToArray()));
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Filter(Stream output, byte[][] rows, int bpp, int[] filters)
    {
        byte[]? prev = null;
        for (var r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            var up = prev ?? new byte[row.Length];
            output.WriteByte((byte)filters[r]);
            for (var i = 0; i < row.Length; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = up[i], c = i >= bpp ? up[i - bpp] : 0;
                var predictor = filters[r] switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    _ => Paeth(a, b, c),
                };
                output.WriteByte((byte)(row[i] - predictor));
            }
            prev = row;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        png.Write(len);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(typed));
        png.Write(len);
    }

    private static byte[] ZlibStored(byte[] data)
    {
        var z = new MemoryStream();
        z.Write([0x78, 0x01]);
        var at = 0;
        do
        {
            var n = Math.Min(65535, data.Length - at);
            z.WriteByte((byte)(at + n == data.Length ? 1 : 0));
            z.Write([(byte)n, (byte)(n >> 8), (byte)~n, (byte)(~n >> 8)]);
            z.Write(data, at, n);
            at += n;
        } while (at < data.Length);
        uint s1 = 1, s2 = 0;
        foreach (var b in data)
        {
            s1 = (s1 + b) % 65521;
            s2 = (s2 + s1) % 65521;
        }
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, (s2 << 16) | s1);
        z.Write(adler);
        return z.ToArray();
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xffffffffu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
