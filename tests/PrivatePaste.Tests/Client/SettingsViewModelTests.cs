using PrivatePaste.Desktop.Input;
using PrivatePaste.Desktop.ViewModels;

namespace PrivatePaste.Tests.Client;

public class SettingsViewModelTests
{
    private readonly List<HotkeyGesture> _applied = [];
    private readonly List<bool> _autostart = [];
    private readonly List<bool> _notify = [];
    private int _signOuts;

    private SettingsViewModel Create(string hotkey = "Ctrl+Shift+V", bool autostart = true, bool notify = true) => new(
        server: "https://paste.example.com", deviceName: "PC-work", hotkey: hotkey, autostart: autostart,
        autostartLabel: "Start with Windows",
        applyHotkey: g => _applied.Add(g), setAutostart: v => _autostart.Add(v),
        signOut: () => { _signOuts++; return Task.CompletedTask; },
        notifyOnClipArrival: notify, setNotifyOnClipArrival: v => _notify.Add(v));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Notify_on_clip_arrival_shows_the_setting_and_calls_back_on_change(bool initial)
    {
        var vm = Create(notify: initial);
        Assert.Equal(initial, vm.NotifyOnClipArrival);
        Assert.Empty(_notify);   // showing the stored value is not a change
        vm.NotifyOnClipArrival = !initial;
        vm.NotifyOnClipArrival = initial;
        Assert.Equal([!initial, initial], _notify);
    }

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

    // ---- Private copies: blocked apps ----

    private readonly List<IReadOnlyList<string>> _blockedSaved = [];

    private SettingsViewModel CreateBlocking(string[]? blocked = null, string[]? recent = null, bool canIdentify = true) => new(
        server: "https://paste.example.com", deviceName: "PC-work", hotkey: "Ctrl+Shift+V", autostart: true,
        autostartLabel: "Start with Windows", applyHotkey: _ => { }, setAutostart: _ => { }, signOut: () => Task.CompletedTask,
        blockedApps: blocked, recentSourceApps: () => recent ?? [], setBlockedApps: l => _blockedSaved.Add(l),
        canIdentifySourceApp: canIdentify);

    [Fact]
    public void Shows_the_stored_blocklist()
    {
        var vm = CreateBlocking(blocked: ["keepassxc", "1password"]);
        Assert.Equal(["keepassxc", "1password"], vm.BlockedApps);
        Assert.Empty(_blockedSaved);   // showing the stored list is not a change
    }

    [Fact]
    public void Adding_from_recent_apps_saves_and_leaves_the_menu_without_it()
    {
        var vm = CreateBlocking(blocked: ["keepassxc"], recent: ["Chrome", "keepassxc", "Notepad"]);
        Assert.Equal(["Chrome", "Notepad"], vm.AddableApps);   // not those already listed
        vm.AddBlockedAppCommand.Execute("Chrome");
        Assert.Equal(["keepassxc", "chrome"], vm.BlockedApps);
        Assert.Equal(["keepassxc", "chrome"], Assert.Single(_blockedSaved));
        Assert.Equal(["Notepad"], vm.AddableApps);
    }

    [Theory]
    [InlineData("  KeePassXC.EXE ", "keepassxc")]
    [InlineData("Bitwarden", "bitwarden")]
    [InlineData("com.agilebits.OnePassword7", "com.agilebits.onepassword7")]
    public void A_typed_name_is_normalised(string typed, string stored)
    {
        var vm = CreateBlocking();
        vm.StartTypingAppCommand.Execute(null);
        Assert.True(vm.IsTypingApp);
        vm.TypedApp = typed;
        vm.ConfirmTypedAppCommand.Execute(null);
        Assert.Equal([stored], vm.BlockedApps);
        Assert.Equal([stored], Assert.Single(_blockedSaved));
        Assert.False(vm.IsTypingApp);
        Assert.Equal("", vm.TypedApp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".exe")]
    public void A_blank_typed_name_is_ignored(string typed)
    {
        var vm = CreateBlocking();
        vm.StartTypingAppCommand.Execute(null);
        vm.TypedApp = typed;
        vm.ConfirmTypedAppCommand.Execute(null);
        Assert.Empty(vm.BlockedApps);
        Assert.Empty(_blockedSaved);
    }

    [Fact]
    public void A_name_already_listed_is_not_added_twice()
    {
        var vm = CreateBlocking(blocked: ["keepassxc"]);
        vm.AddBlockedAppCommand.Execute("KeePassXC.exe");
        Assert.Equal(["keepassxc"], vm.BlockedApps);
        Assert.Empty(_blockedSaved);
    }

    [Fact]
    public void Removing_saves_the_rest()
    {
        var vm = CreateBlocking(blocked: ["a", "b"]);
        vm.RemoveBlockedAppCommand.Execute("a");
        Assert.Equal(["b"], vm.BlockedApps);
        Assert.Equal(["b"], Assert.Single(_blockedSaved));
    }

    [Fact]
    public void Cancelling_the_typed_name_closes_the_box()
    {
        var vm = CreateBlocking();
        vm.StartTypingAppCommand.Execute(null);
        vm.TypedApp = "half";
        vm.CancelTypingAppCommand.Execute(null);
        Assert.False(vm.IsTypingApp);
        Assert.Equal("", vm.TypedApp);
    }

    [Fact]
    public void Where_apps_cannot_be_identified_the_section_says_so()
    {
        Assert.True(CreateBlocking().CanBlockApps);
        var vm = CreateBlocking(canIdentify: false);
        Assert.False(vm.CanBlockApps);
        Assert.Equal("This system can't tell which app copied something, so apps can't be blocked here. "
            + "Copies that password managers mark as private are still ignored.", SettingsViewModel.CannotBlockAppsMessage);
    }
}
