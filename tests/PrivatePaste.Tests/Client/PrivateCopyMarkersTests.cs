using PrivatePaste.Desktop.Platform.Windows;

namespace PrivatePaste.Tests.Client;

public class PrivateCopyMarkersTests
{
    private static readonly byte[] Zero = [0, 0, 0, 0];
    private static readonly byte[] One = [1, 0, 0, 0];

    private static bool Windows(params (string Name, byte[]? Data)[] formats) =>
        PrivateCopyMarkers.IsPrivate(formats.ToDictionary(f => f.Name, f => f.Data));

    [Fact]
    public void No_markers_is_not_private() => Assert.False(Windows());

    [Fact]
    public void Unrelated_formats_are_not_private() => Assert.False(Windows(("HTML Format", Zero)));

    [Fact]
    public void Exclude_from_monitor_processing_is_private_whatever_its_content()
    {
        Assert.True(Windows(("ExcludeClipboardContentFromMonitorProcessing", null)));
        Assert.True(Windows(("ExcludeClipboardContentFromMonitorProcessing", One)));
    }

    [Fact]
    public void Clipboard_viewer_ignore_is_private_whatever_its_content()
    {
        Assert.True(Windows(("Clipboard Viewer Ignore", null)));
        Assert.True(Windows(("Clipboard Viewer Ignore", One)));
    }

    [Theory]
    [InlineData("CanIncludeInClipboardHistory")]
    [InlineData("CanUploadToCloudClipboard")]
    public void Dword_marker_is_private_only_when_zero(string name)
    {
        Assert.True(Windows((name, Zero)));
        Assert.False(Windows((name, One)));
        Assert.False(Windows((name, [0, 1, 0, 0])));   // little-endian 256
        Assert.False(Windows((name, null)));            // present but unreadable: do not guess
        Assert.False(Windows((name, [0, 0])));          // too short to be a DWORD
    }

    [Fact]
    public void Allowed_dword_does_not_cancel_a_blocking_marker() =>
        Assert.True(Windows(("CanIncludeInClipboardHistory", One), ("Clipboard Viewer Ignore", null)));
}
