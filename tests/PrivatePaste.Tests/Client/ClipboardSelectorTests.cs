using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Desktop.Platform;
using PrivatePaste.Desktop.Platform.Windows;
using SkiaSharp;

namespace PrivatePaste.Tests.Client;

public class ClipboardSelectorTests
{
    private static byte[] Png()
    {
        using var bmp = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bmp.SetPixel(0, 0, new SKColor(1, 2, 3));
        using var d = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return d.ToArray();
    }

    private static byte[] Text(string s) => System.Text.Encoding.Unicode.GetBytes(s + "\0");

    private static ClipboardContent? Select(Dictionary<ClipboardFormat, byte[]> formats) =>
        ClipboardSelector.Select(f => formats.GetValueOrDefault(f), null);

    [Fact]
    public void Text_and_synthesized_bitmap_gives_text()
    {
        var r = Select(new() { [ClipboardFormat.UnicodeText] = Text("cell"), [ClipboardFormat.Dib] = DibConverter.FromPng(Png()) });
        Assert.Equal(new ClipboardContent.Text("cell"), r);
    }

    [Fact]
    public void Png_and_dib_gives_image()
    {
        var png = Png();
        var r = Select(new() { [ClipboardFormat.Png] = png, [ClipboardFormat.Dib] = DibConverter.FromPng(png) });
        Assert.Equal(png, Assert.IsType<ClipboardContent.Image>(r).Png);
    }

    [Fact]
    public void Whitespace_text_with_png_gives_image()
    {
        var r = Select(new() { [ClipboardFormat.UnicodeText] = Text("   "), [ClipboardFormat.Png] = Png() });
        Assert.IsType<ClipboardContent.Image>(r);
    }

    [Fact]
    public void Dib_only_converts_and_unsupported_dib_falls_through_to_null()
    {
        Assert.IsType<ClipboardContent.Image>(Select(new() { [ClipboardFormat.Dib] = DibConverter.FromPng(Png()) }));
        Assert.Null(Select(new() { [ClipboardFormat.Dib] = [1, 2, 3] }));
        Assert.Null(Select(new()));
    }
}
