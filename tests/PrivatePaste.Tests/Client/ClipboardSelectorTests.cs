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

    /// <summary>Records which formats a capture asked for.</summary>
    private static (Dictionary<ClipboardFormat, byte[]> Captured, List<ClipboardFormat> Asked) Capture(Dictionary<ClipboardFormat, byte[]> formats)
    {
        var asked = new List<ClipboardFormat>();
        var captured = ClipboardSelector.Capture(f => { asked.Add(f); return formats.GetValueOrDefault(f); });
        return (captured, asked);
    }

    [Fact]
    public void Capture_copies_raw_bytes_only_and_converts_nothing()
    {
        var dib = DibConverter.FromPng(Png());
        var (captured, _) = Capture(new() { [ClipboardFormat.Dib] = dib });
        Assert.Same(dib, captured[ClipboardFormat.Dib]);   // still a DIB: conversion happens after the session closed
        Assert.IsType<ClipboardContent.Image>(ClipboardSelector.Select(captured, null));
    }

    [Fact]
    public void Capture_skips_formats_the_selection_would_not_use()
    {
        var (_, textAsked) = Capture(new() { [ClipboardFormat.UnicodeText] = Text("cell"), [ClipboardFormat.Png] = Png() });
        Assert.Equal([ClipboardFormat.UnicodeText], textAsked);
        var (_, pngAsked) = Capture(new() { [ClipboardFormat.Png] = Png(), [ClipboardFormat.Dib] = [1] });
        Assert.Equal([ClipboardFormat.UnicodeText, ClipboardFormat.Png], pngAsked);
    }

    [Fact]
    public void Busy_rearm_allows_three_retries_per_change()
    {
        var rearm = new ClipboardRearm();
        Assert.True(rearm.TryRearm(7));
        Assert.True(rearm.TryRearm(7));
        Assert.True(rearm.TryRearm(7));
        Assert.False(rearm.TryRearm(7));
        Assert.True(rearm.TryRearm(8));   // a new change starts over
    }
}
