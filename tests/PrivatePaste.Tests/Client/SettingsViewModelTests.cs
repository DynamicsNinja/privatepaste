using PrivatePaste.Desktop.Input;
using PrivatePaste.Desktop.ViewModels;

namespace PrivatePaste.Tests.Client;

public class SettingsViewModelTests
{
    private readonly List<HotkeyGesture> _applied = [];
    private readonly List<bool> _autostart = [];
    private int _signOuts;

    private SettingsViewModel Create(string hotkey = "Ctrl+Shift+V", bool autostart = true) => new(
        server: "https://paste.example.com", deviceName: "PC-work", hotkey: hotkey, autostart: autostart,
        autostartLabel: "Start with Windows",
        applyHotkey: g => _applied.Add(g), setAutostart: v => _autostart.Add(v),
        signOut: () => { _signOuts++; return Task.CompletedTask; });

    [Fact]
    public void Shows_current_values()
    {
        var vm = Create();
        Assert.Equal("https://paste.example.com", vm.Server);
        Assert.Equal("PC-work", vm.DeviceName);
        Assert.Equal("Ctrl+Shift+V", vm.Hotkey);
        Assert.True(vm.StartWithOs);
        Assert.Equal("Start with Windows", vm.AutostartLabel);
        Assert.Null(vm.HotkeyError);
    }

    [Fact]
    public void Invalid_hotkey_shows_error_and_is_not_applied()
    {
        var vm = Create();
        vm.Hotkey = "V";
        vm.ApplyHotkeyCommand.Execute(null);
        Assert.Equal("Use a combination like Ctrl+Shift+V.", vm.HotkeyError);
        Assert.Empty(_applied);
    }

    [Fact]
    public void Valid_hotkey_is_applied_in_canonical_form_and_clears_the_error()
    {
        var vm = Create();
        vm.Hotkey = "Ctrl+V+B";
        vm.ApplyHotkeyCommand.Execute(null);
        Assert.NotNull(vm.HotkeyError);

        vm.Hotkey = "alt+ctrl+h";
        Assert.Null(vm.HotkeyError);   // editing clears the error
        vm.ApplyHotkeyCommand.Execute(null);
        Assert.Null(vm.HotkeyError);
        Assert.Equal("Ctrl+Alt+H", vm.Hotkey);
        var g = Assert.Single(_applied);
        Assert.Equal("Ctrl+Alt+H", g.ToString());
    }

    [Fact]
    public void Applying_the_unchanged_hotkey_does_nothing()
    {
        var vm = Create();
        vm.ApplyHotkeyCommand.Execute(null);
        Assert.Empty(_applied);
        Assert.Null(vm.HotkeyError);
    }

    [Fact]
    public void Toggling_autostart_calls_back()
    {
        var vm = Create(autostart: true);
        vm.StartWithOs = false;
        vm.StartWithOs = true;
        Assert.Equal([false, true], _autostart);
    }

    [Fact]
    public async Task Sign_out_calls_back()
    {
        var vm = Create();
        await vm.SignOutCommand.ExecuteAsync(null);
        Assert.Equal(1, _signOuts);
    }
}
