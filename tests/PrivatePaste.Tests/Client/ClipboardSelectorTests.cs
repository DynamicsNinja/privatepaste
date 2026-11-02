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
        Assert.Equal([ClipboardFormat.HDrop, ClipboardFormat.UnicodeText], textAsked);
        var (_, pngAsked) = Capture(new() { [ClipboardFormat.Png] = Png(), [ClipboardFormat.Dib] = [1] });
        Assert.Equal([ClipboardFormat.HDrop, ClipboardFormat.UnicodeText, ClipboardFormat.Png], pngAsked);
    }

    [Fact]
    public void Files_win_over_the_file_name_text()
    {
        // Explorer offers the names as text too; the files are what was copied.
        string[] paths = [@"C:\Users\Ana\report.pdf", @"C:\Users\Ana\notes.txt"];
        var r = Select(new()
        {
            [ClipboardFormat.HDrop] = DropFiles.Build(paths),
            [ClipboardFormat.UnicodeText] = Text("report.pdf"),
        });
        Assert.Equal(paths, Assert.IsType<ClipboardContent.Files>(r).Paths);
    }

    [Fact]
    public void An_image_next_to_a_file_list_stays_an_image()
    {
        // Chat apps and screenshot tools put a temp file next to the bitmap; Explorer never adds a bitmap to a file copy.
        var drop = DropFiles.Build([@"C:\Temp\shot.png"]);
        var png = Png();
        Assert.Equal(png, Assert.IsType<ClipboardContent.Image>(Select(new()
        {
            [ClipboardFormat.HDrop] = drop, [ClipboardFormat.UnicodeText] = Text("shot.png"), [ClipboardFormat.Png] = png,
        })).Png);
        Assert.IsType<ClipboardContent.Image>(Select(new() { [ClipboardFormat.HDrop] = drop, [ClipboardFormat.Dib] = DibConverter.FromPng(png) }));
        // A bitmap that doesn't convert: the files are still there.
        Assert.IsType<ClipboardContent.Files>(Select(new() { [ClipboardFormat.HDrop] = drop, [ClipboardFormat.Dib] = [1, 2, 3] }));

        var (captured, asked) = Capture(new() { [ClipboardFormat.HDrop] = drop, [ClipboardFormat.Png] = png, [ClipboardFormat.UnicodeText] = Text("x") });
        Assert.Equal([ClipboardFormat.HDrop, ClipboardFormat.Png], asked);   // the names as text are never needed
        Assert.IsType<ClipboardContent.Image>(ClipboardSelector.Select(captured, null));
    }

    [Fact]
    public void Capture_takes_only_the_file_list_when_there_is_no_image()
    {
        var drop = DropFiles.Build([@"C:\a.txt"]);
        var (captured, asked) = Capture(new() { [ClipboardFormat.HDrop] = drop, [ClipboardFormat.UnicodeText] = Text("a.txt") });
        Assert.Equal([ClipboardFormat.HDrop, ClipboardFormat.Png, ClipboardFormat.DibV5, ClipboardFormat.Dib], asked);
        Assert.Single(captured);
        Assert.Equal([@"C:\a.txt"], Assert.IsType<ClipboardContent.Files>(ClipboardSelector.Select(captured, null)).Paths);
    }

    [Fact]
    public void An_unreadable_file_list_falls_through_to_text()
    {
        var r = Select(new() { [ClipboardFormat.HDrop] = [1, 2, 3], [ClipboardFormat.UnicodeText] = Text("cell") });
        Assert.Equal(new ClipboardContent.Text("cell"), r);
        var (captured, _) = Capture(new() { [ClipboardFormat.HDrop] = [1, 2, 3], [ClipboardFormat.UnicodeText] = Text("cell") });
        Assert.Equal(new ClipboardContent.Text("cell"), ClipboardSelector.Select(captured, null));
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
