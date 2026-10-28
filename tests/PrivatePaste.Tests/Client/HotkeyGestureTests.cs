using PrivatePaste.Desktop.Input;
using SharpHook.Data;

namespace PrivatePaste.Tests.Client;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+V")]
    [InlineData("ctrl+shift+v")]
    [InlineData("Cmd+Shift+V")]
    [InlineData("Alt+F2")]
    public void Parses(string s)
    {
        Assert.True(HotkeyGesture.TryParse(s, out var g));
        Assert.NotNull(g);
    }

    [Theory]
    [InlineData("V")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+V+B")]
    [InlineData("")]
    public void Rejects(string s)
    {
        Assert.False(HotkeyGesture.TryParse(s, out var g));
        Assert.Null(g);
    }

    [Fact]
    public void Canonical_to_string()
    {
        Assert.True(HotkeyGesture.TryParse("shift+ctrl+v", out var g));
        Assert.Equal("Ctrl+Shift+V", g!.ToString());
    }

    [Fact]
    public void Parsed_fields()
    {
        Assert.True(HotkeyGesture.TryParse("Cmd+Shift+V", out var mac));
        Assert.Equal(new HotkeyGesture(Ctrl: false, Shift: true, Alt: false, Meta: true, KeyCode.VcV), mac);
        Assert.True(HotkeyGesture.TryParse(" Alt + F2 ", out var alt));
        Assert.Equal(new HotkeyGesture(false, false, true, false, KeyCode.VcF2), alt);
    }

    [Theory]
    [InlineData("Ctrl+LeftShift")]   // a modifier key is not the main key
    [InlineData("Ctrl+Ctrl+V")]
    [InlineData("Ctrl+42")]
    [InlineData("Ctrl+V,B")]
    [InlineData("Ctrl+Undefined")]
    [InlineData("Ctrl++V")]
    [InlineData(null)]
    public void Rejects_odd_input(string? s) => Assert.False(HotkeyGesture.TryParse(s!, out _));
}
