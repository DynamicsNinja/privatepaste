using PrivatePaste.Desktop.Platform;

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
    public void Readable_dword_marker_is_private_only_when_zero(string name)
    {
        Assert.True(Windows((name, Zero)));
        Assert.False(Windows((name, One)));
        Assert.False(Windows((name, [0, 1, 0, 0])));   // little-endian 256
    }

    [Theory]
    [InlineData("CanIncludeInClipboardHistory")]
    [InlineData("CanUploadToCloudClipboard")]
    public void Dword_marker_that_cannot_be_read_fails_closed(string name)
    {
        Assert.True(Windows((name, null)));        // present but unreadable
        Assert.True(Windows((name, [])));
        Assert.True(Windows((name, [1, 0])));      // too short to be a DWORD
    }

    [Fact]
    public void Allowed_dword_does_not_cancel_a_blocking_marker() =>
        Assert.True(Windows(("CanIncludeInClipboardHistory", One), ("Clipboard Viewer Ignore", null)));

    [Fact]
    public void Mac_concealed_or_transient_type_is_private()
    {
        Assert.True(PrivateCopyMarkers.IsPrivateMac(["public.utf8-plain-text", "org.nspasteboard.ConcealedType"]));
        Assert.True(PrivateCopyMarkers.IsPrivateMac(["org.nspasteboard.TransientType"]));
    }

    [Fact]
    public void Mac_ordinary_or_empty_types_are_not_private()
    {
        Assert.False(PrivateCopyMarkers.IsPrivateMac([]));
        Assert.False(PrivateCopyMarkers.IsPrivateMac(["public.utf8-plain-text", "org.nspasteboard.source"]));
        Assert.False(PrivateCopyMarkers.IsPrivateMac(["org.nspasteboard.concealedtype"]));   // names are case-sensitive
    }

    private static bool Linux(string[] targets, string? value) =>
        PrivateCopyMarkers.IsPrivateLinux(targets, _ => value);

    [Fact]
    public void Linux_password_manager_hint_secret_is_private() =>
        Assert.True(Linux(["UTF8_STRING", "x-kde-passwordManagerHint"], "secret"));

    [Fact]
    public void Linux_hint_value_is_trimmed() =>
        Assert.True(Linux(["x-kde-passwordManagerHint"], "secret\n"));

    [Fact]
    public void Linux_unreadable_hint_fails_closed() =>
        Assert.True(Linux(["x-kde-passwordManagerHint"], null));

    [Fact]
    public void Linux_hint_with_another_value_is_not_private() =>
        Assert.False(Linux(["x-kde-passwordManagerHint"], "normal"));

    [Fact]
    public void Linux_without_the_hint_is_not_private_and_reads_nothing()
    {
        var read = false;
        Assert.False(PrivateCopyMarkers.IsPrivateLinux(["UTF8_STRING", "text/plain"], _ => { read = true; return "secret"; }));
        Assert.False(read);
    }

    [Fact]
    public void Linux_reader_that_throws_fails_closed() =>
        Assert.True(PrivateCopyMarkers.IsPrivateLinux(["x-kde-passwordManagerHint"], _ => throw new IOException()));

    // The async form is what the Linux clipboard uses: the hint is read through Avalonia's async data API.

    [Fact]
    public async Task Linux_async_reader_that_throws_fails_closed()
    {
        Assert.True(await PrivateCopyMarkers.IsPrivateLinuxAsync(["x-kde-passwordManagerHint"],
            async _ => { await Task.Yield(); throw new InvalidOperationException(); }));
        Assert.True(await PrivateCopyMarkers.IsPrivateLinuxAsync(["x-kde-passwordManagerHint"],
            _ => throw new InvalidOperationException()));
    }

    [Fact]
    public async Task Linux_async_reader_reads_the_hint_value()
    {
        Assert.True(await PrivateCopyMarkers.IsPrivateLinuxAsync(["x-kde-passwordManagerHint"], _ => Task.FromResult<string?>("secret")));
        Assert.True(await PrivateCopyMarkers.IsPrivateLinuxAsync(["x-kde-passwordManagerHint"], _ => Task.FromResult<string?>(null)));
        Assert.False(await PrivateCopyMarkers.IsPrivateLinuxAsync(["x-kde-passwordManagerHint"], _ => Task.FromResult<string?>("normal")));
    }

    [Fact]
    public async Task Linux_async_without_the_hint_reads_nothing()
    {
        var read = false;
        Assert.False(await PrivateCopyMarkers.IsPrivateLinuxAsync(["UTF8_STRING"],
            _ => { read = true; return Task.FromResult<string?>("secret"); }));
        Assert.False(read);
    }
}
