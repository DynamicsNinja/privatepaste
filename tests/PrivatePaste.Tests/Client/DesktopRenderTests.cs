using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Styling;
using PrivatePaste.Desktop;
using PrivatePaste.Desktop.ViewModels;
using PrivatePaste.Desktop.Views;

namespace PrivatePaste.Tests.Client;

/// <summary>Entry point for the headless Avalonia session: the real App (theme only) rendered with Skia.</summary>
public static class HeadlessDesktopApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Loads and renders the windows with the Night vault theme (catches XAML, binding and resource errors).
/// Set PP_SCREENSHOT_DIR to also save PNGs.
/// </summary>
public class DesktopRenderTests
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessDesktopApp)));

    private static readonly string? ScreenshotDir = Environment.GetEnvironmentVariable("PP_SCREENSHOT_DIR");

    [Fact]
    public Task Sora_is_bundled() => Session.Value.Dispatch(() =>
    {
        var family = (FontFamily)Application.Current!.FindResource("PpFont")!;
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyphs));
        Assert.Equal("Sora", glyphs!.FamilyName);
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Sign_in_window_renders(string theme) => Session.Value.Dispatch(() =>
    {
        var vm = new SignInViewModel((_, _, _, _) => Task.FromResult(new SignInOutcome(SignInResult.Ok, null)))
        {
            ServerUrl = "http://localhost:5299",
            Username = "Ana",
            Password = "dev",
            DeviceName = "PC-work",
            Error = SignInViewModel.WrongPasswordMessage,
        };
        Render(new SignInWindow { DataContext = vm }, theme, "signin");
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Settings_window_renders(string theme) => Session.Value.Dispatch(() =>
    {
        var vm = new SettingsViewModel("http://localhost:5299", "PC-work", "Ctrl+Shift+V", true, "Start with Windows",
            _ => { }, _ => { }, () => Task.CompletedTask);
        Render(new SettingsWindow { DataContext = vm }, theme, "settings");
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark", true)]
    [InlineData("light", true)]
    [InlineData("dark", false)]
    [InlineData("light", false)]
    public Task Settings_window_renders_private_copies(string theme, bool canIdentify) => Session.Value.Dispatch(() =>
    {
        var vm = new SettingsViewModel("http://localhost:5299", "PC-work", "Ctrl+Shift+V", true, "Start with Windows",
            _ => { }, _ => { }, () => Task.CompletedTask,
            blockedApps: canIdentify ? ["keepassxc", "com.agilebits.onepassword7"] : [],
            recentSourceApps: () => ["chrome", "notepad"], setBlockedApps: _ => { }, canIdentifySourceApp: canIdentify);
        Render(new SettingsWindow { DataContext = vm }, theme, canIdentify ? "settings-private" : "settings-private-unavailable", w =>
        {
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Private copies", texts);
            Assert.Equal(canIdentify, texts.Contains("Ignore copies from these apps"));
            Assert.Equal(!canIdentify, texts.Contains(SettingsViewModel.CannotBlockAppsMessage));
        });
    }, CancellationToken.None);

    [Fact]
    public Task Toast_window_renders() => Session.Value.Dispatch(() =>
    {
        Render(new ToastWindow { Message = "Clip too large to sync (text over 1 MB)." }, "dark", "toast", w =>
        {
            Assert.True(Find<StackPanel>(w, "MessagePanel").IsVisible);
            Assert.False(Find<Grid>(w, "ClipPanel").IsVisible);
        });
    }, CancellationToken.None);

    private static PrivatePaste.Protocol.ClipDto ToastClip(PrivatePaste.Protocol.ClipKind kind, string? text, string device,
        string? platform, int? w = null, int? h = null) =>
        new(Guid.NewGuid(), 1, kind, text, "h", w, h, 100, Guid.NewGuid(), device, DateTimeOffset.UtcNow)
        {
            SourceDevicePlatform = platform,
        };

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Clip_toast_renders(string theme) => Session.Value.Dispatch(() =>
    {
        var toast = PrivatePaste.Desktop.Tray.ClipToast.For(ToastClip(PrivatePaste.Protocol.ClipKind.Text,
            "\n  ssh deploy@paste.example.com 'docker compose pull && docker compose up -d --remove-orphans --wait'\nexit",
            "Pixel 9", "android"));
        var window = new ToastWindow();
        window.Message = "an earlier message";   // a clip replaces the message in the same window
        window.ShowClip(toast);
        Render(window, theme, "toast-clip", w =>
        {
            Assert.False(Find<StackPanel>(w, "MessagePanel").IsVisible);
            Assert.True(Find<Grid>(w, "ClipPanel").IsVisible);
            Assert.Equal("From Pixel 9", Find<TextBlock>(w, "ClipTitle").Text);
            var preview = Find<TextBlock>(w, "ClipPreview").Text!;
            Assert.Equal(PrivatePaste.Desktop.Tray.ClipToast.PreviewLength, preview.Length);
            Assert.EndsWith("…", preview);
            Assert.Equal("Android", Avalonia.Automation.AutomationProperties.GetName(Find<Viewbox>(w, "ClipPlatformIcon")));
            Assert.IsType<StreamGeometry>(Find<Avalonia.Controls.Shapes.Path>(w, "ClipPlatformPath").Data);
            Assert.True(w.Bounds.Width <= 362, $"toast is {w.Bounds.Width} wide");
            Assert.False(w.ShowActivated);   // never steals the focus
        });

        // A second clip right after updates the same card in place.
        var image = PrivatePaste.Desktop.Tray.ClipToast.For(ToastClip(PrivatePaste.Protocol.ClipKind.Image, null, "", "web", 1280, 720));
        var again = new ToastWindow();
        again.ShowClip(toast);
        again.ShowClip(image);
        Render(again, theme, "toast-clip-image", w =>
        {
            Assert.Equal("From another device", Find<TextBlock>(w, "ClipTitle").Text);
            Assert.Equal("Image 1280×720", Find<TextBlock>(w, "ClipPreview").Text);
            Assert.Equal("Web", Avalonia.Automation.AutomationProperties.GetName(Find<Viewbox>(w, "ClipPlatformIcon")));
        });
    }, CancellationToken.None);

    [Theory]
    [InlineData("short", "short")]
    [InlineData("\n\n   \n  indented first line  \nsecond", "indented first line")]
    [InlineData("a\r\nb", "a")]
    [InlineData("", "")]
    public void Clip_toast_preview_is_the_first_non_empty_line(string text, string expected) =>
        Assert.Equal(expected, PrivatePaste.Desktop.Tray.ClipToast.FormatPreview(
            ToastClip(PrivatePaste.Protocol.ClipKind.Text, text, "PC", "windows")));

    [Fact]
    public void Clip_toast_preview_is_cut_to_80_chars_with_an_ellipsis()
    {
        var exactly = new string('x', 80);
        Assert.Equal(exactly, PrivatePaste.Desktop.Tray.ClipToast.FormatPreview(
            ToastClip(PrivatePaste.Protocol.ClipKind.Text, exactly, "PC", "windows")));
        var cut = PrivatePaste.Desktop.Tray.ClipToast.FormatPreview(
            ToastClip(PrivatePaste.Protocol.ClipKind.Link, new string('y', 81), "PC", "windows"));
        Assert.Equal(new string('y', 79) + "…", cut);
    }

    [Theory]
    [InlineData("Ana", "Work laptop", "From Ana · Work laptop")]
    [InlineData("Ana", "", "From Ana")]
    [InlineData(null, "Work laptop", "From Work laptop")]
    [InlineData("  ", "Work laptop", "From Work laptop")]
    public void Toast_Title_WithAndWithoutPerson(string? person, string device, string title)
    {
        var clip = ToastClip(PrivatePaste.Protocol.ClipKind.Text, "x", device, "windows") with { SourceUserName = person };
        Assert.Equal(title, PrivatePaste.Desktop.Tray.ClipToast.For(clip).Title);
    }

    [Theory]
    [InlineData("MacBook", "macos", "From MacBook", "macos")]
    [InlineData("  ", "linux", "From another device", "linux")]
    [InlineData(null, null, "From another device", "unknown")]
    public void Clip_toast_title_and_icon(string? device, string? platform, string title, string key)
    {
        var toast = PrivatePaste.Desktop.Tray.ClipToast.For(
            ToastClip(PrivatePaste.Protocol.ClipKind.Image, null, device!, platform, 640, 480));
        Assert.Equal(title, toast.Title);
        Assert.Equal(key, toast.Platform.Key);
        Assert.Equal("Image 640×480", toast.Preview);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Popup_window_renders(string theme) => Session.Value.Dispatch(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PrivatePaste.Client.Core.Cache.HistoryCache(
            Path.Combine(Path.GetTempPath(), "pp-render-" + Guid.NewGuid() + ".dat"), new byte[32]);
        PrivatePaste.Protocol.ClipDto Clip(int seq, PrivatePaste.Protocol.ClipKind kind, string? text, int minutes, string device,
            string? platform, int? w = null, int? h = null) =>
            new(Guid.NewGuid(), seq, kind, text, "h" + seq, w, h, 100, null, device, now.AddMinutes(-minutes))
            {
                SourceDevicePlatform = platform,
            };
        // One row per platform icon, plus an unrecognized and a missing platform (both "Unknown device").
        cache.Upsert(Clip(7, PrivatePaste.Protocol.ClipKind.Text, "git commit -m \"fix(client): sync engine resume\"\nsecond line", 0, "PC-work", "windows"));
        cache.Upsert(Clip(6, PrivatePaste.Protocol.ClipKind.Link, "https://paste.example.com/some/long/path?with=query&and=more", 4, "MacBook", "macos"));
        cache.Upsert(Clip(5, PrivatePaste.Protocol.ClipKind.Image, null, 35, "ThinkPad", "linux", 1280, 720));
        cache.Upsert(Clip(4, PrivatePaste.Protocol.ClipKind.Text, new string('x', 200), 190, "Phone", "android"));
        cache.Upsert(Clip(3, PrivatePaste.Protocol.ClipKind.Text, "Copied from the browser", 600, "Web", "web"));
        cache.Upsert(Clip(2, PrivatePaste.Protocol.ClipKind.Text, "Meeting notes", 3000, "Old laptop", null));
        cache.Upsert(Clip(1, PrivatePaste.Protocol.ClipKind.Text, "Fridge shopping list", 4000, "Fridge", "tizen"));
        var vm = new PopupViewModel(cache, (_, _) => Task.FromResult<IReadOnlyList<PrivatePaste.Protocol.ClipDto>>([]),
            _ => Task.CompletedTask, TimeProvider.System);
        vm.MoveSelection(1);
        var window = new PopupWindow { DataContext = vm };
        Render(window, theme, "popup", w =>
        {
            var icons = w.GetVisualDescendants().OfType<Viewbox>().Where(v => v.Name == "PlatformIcon").ToList();
            Assert.True(icons.Count >= 6, $"only {icons.Count} platform icons realized");
            Assert.Equal(["Windows", "macOS", "Linux", "Android", "Web", "Unknown device"],
                icons.Take(6).Select(v => Avalonia.Automation.AutomationProperties.GetName(v)));
            foreach (var icon in icons)
            {
                var path = icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
                Assert.IsType<StreamGeometry>(path.Data);
                Assert.Equal((Color)Application.Current!.FindResource(w.ActualThemeVariant, "PpMutedColor")!,
                    ((ISolidColorBrush)path.Stroke!).Color);
            }
        });
        Assert.True(vm.Items[0].IsNewest);
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark", false)]
    [InlineData("light", false)]
    [InlineData("dark", true)]
    [InlineData("light", true)]
    public Task Popup_window_renders_pins_and_tabs(string theme, bool pinnedTab) => Session.Value.Dispatch(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PrivatePaste.Client.Core.Cache.HistoryCache(
            Path.Combine(Path.GetTempPath(), "pp-render-" + Guid.NewGuid() + ".dat"), new byte[32]);
        PrivatePaste.Protocol.ClipDto Clip(int seq, string text, int minutes, string device, string platform, int? pinnedMinutes = null) =>
            new(Guid.NewGuid(), seq, PrivatePaste.Protocol.ClipKind.Text, text, "h" + seq, null, null, 100, null, device, now.AddMinutes(-minutes))
            {
                SourceDevicePlatform = platform,
                PinnedAt = pinnedMinutes is { } p ? now.AddMinutes(-p) : null,
            };
        cache.Upsert(Clip(5, "git commit -m \"fix(client): sync engine resume\"", 0, "PC-work", "windows"));
        cache.Upsert(Clip(4, "Meeting room code 4471", 12, "MacBook", "macos", pinnedMinutes: 10));
        cache.Upsert(Clip(3, "ssh deploy@paste.example.com", 90, "ThinkPad", "linux"));
        cache.Upsert(Clip(2, "Wi-Fi password for the guest network", 600, "Phone", "android", pinnedMinutes: 500));
        cache.Upsert(Clip(1, "IBAN DE89 3704 0044 0532 0130 00", 3000, "Web", "web", pinnedMinutes: 2000));
        var vm = new PopupViewModel(cache, (_, _) => Task.FromResult<IReadOnlyList<PrivatePaste.Protocol.ClipDto>>([]),
            _ => Task.CompletedTask, TimeProvider.System);
        if (pinnedTab) vm.SwitchTab();
        vm.MoveSelection(1);
        Render(new PopupWindow { DataContext = vm }, theme, pinnedTab ? "popup-pinned" : "popup-history", w =>
        {
            Assert.Equal(pinnedTab ? 3 : 5, vm.Items.Count);
            // Nothing in the popup may be focusable (the Windows popup never takes the focus).
            Assert.All(w.GetVisualDescendants().OfType<Button>(), b => Assert.False(b.Focusable));
            Assert.Equal(vm.Items.Count, w.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("pin")));
            Assert.Equal(pinnedTab ? "Tab for History" : "Tab for Pinned",
                w.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("Enter to paste") == true).Text!
                    .Split("   ")[1]);
        });
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Popup_window_renders_person_chips(string theme) => Session.Value.Dispatch(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PrivatePaste.Client.Core.Cache.HistoryCache(
            Path.Combine(Path.GetTempPath(), "pp-render-" + Guid.NewGuid() + ".dat"), new byte[32]);
        PrivatePaste.Protocol.ClipDto Clip(int seq, string text, int minutes, string device, string platform, string? person) =>
            new(Guid.NewGuid(), seq, PrivatePaste.Protocol.ClipKind.Text, text, "h" + seq, null, null, 100, null, device,
                now.AddMinutes(-minutes))
            { SourceDevicePlatform = platform, SourceUserName = person };
        cache.Upsert(Clip(4, "git commit -m \"fix(client): sync engine resume\"", 0, "PC-work", "windows", "Ana"));
        cache.Upsert(Clip(3, "https://paste.example.com/some/long/path?with=query", 4, "Pixel 9", "android", "Marko"));
        cache.Upsert(Clip(2, "Meeting room code 4471", 35, "MacBook", "macos", "Ana"));
        cache.Upsert(Clip(1, "A clip from before there were people", 190, "ThinkPad", "linux", null));
        var vm = new PopupViewModel(cache, (_, _) => Task.FromResult<IReadOnlyList<PrivatePaste.Protocol.ClipDto>>([]),
            _ => Task.CompletedTask, TimeProvider.System);
        Render(new PopupWindow { DataContext = vm }, theme, "popup-people", w =>
        {
            var chips = w.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PersonChip" && b.IsEffectivelyVisible).ToList();
            Assert.Equal(["Ana", "Marko", "Ana"], chips.Select(c => ((TextBlock)c.Child!).Text));
            Assert.All(chips, c => Assert.False(c.Focusable));
        });
    }, CancellationToken.None);

    /// <summary>
    /// The README hero's popup, from the demo history in scripts/readme/demo.json (see scripts/readme/README.md).
    /// Runs only when PP_README_DEMO names that file; saves readme-popup-{theme}.png into PP_SCREENSHOT_DIR.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Readme_popup(string theme) => Session.Value.Dispatch(() =>
    {
        if (Environment.GetEnvironmentVariable("PP_README_DEMO") is not { Length: > 0 } demoPath) return;
        var now = DateTimeOffset.UtcNow;
        var cache = new PrivatePaste.Client.Core.Cache.HistoryCache(
            Path.Combine(Path.GetTempPath(), "pp-render-" + Guid.NewGuid() + ".dat"), new byte[32]);
        using var demo = System.Text.Json.JsonDocument.Parse(File.ReadAllText(demoPath));
        var clips = demo.RootElement.GetProperty("clips").EnumerateArray().ToList();
        for (var i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            var kind = Enum.Parse<PrivatePaste.Protocol.ClipKind>(c.GetProperty("kind").GetString()!, ignoreCase: true);
            int? Size(string name) => c.TryGetProperty(name, out var v) ? v.GetInt32() : null;
            cache.Upsert(new PrivatePaste.Protocol.ClipDto(Guid.NewGuid(), clips.Count - i, kind,
                c.TryGetProperty("text", out var text) ? text.GetString() : null, "h" + i, Size("width"), Size("height"), 100,
                null, c.GetProperty("device").GetString()!, now.AddMinutes(-c.GetProperty("minutesAgo").GetInt32()))
            {
                SourceDevicePlatform = c.GetProperty("platform").GetString(),
                SourceUserName = c.TryGetProperty("user", out var user) ? user.GetString() : null,
                PinnedAt = c.TryGetProperty("pinned", out var pinned) && pinned.GetBoolean()
                    ? now.AddMinutes(-c.GetProperty("minutesAgo").GetInt32() + 1) : null,
            });
        }
        var vm = new PopupViewModel(cache, (_, _) => Task.FromResult<IReadOnlyList<PrivatePaste.Protocol.ClipDto>>([]),
            _ => Task.CompletedTask, TimeProvider.System);
        vm.MoveSelection(1);
        Render(new PopupWindow { DataContext = vm }, theme, "readme-popup");
    }, CancellationToken.None);

    [Theory]
    [InlineData("windows", "windows", "Windows")]
    [InlineData("macos", "macos", "macOS")]
    [InlineData("linux", "linux", "Linux")]
    [InlineData("android", "android", "Android")]
    [InlineData("web", "web", "Web")]
    [InlineData("MacOS", "macos", "macOS")]
    [InlineData("haiku", "unknown", "Unknown device")]
    [InlineData(null, "unknown", "Unknown device")]
    public void Platform_icon_mapping(string? platform, string key, string label)
    {
        var icon = PrivatePaste.Desktop.Theme.PlatformIcons.For(platform);
        Assert.Equal(key, icon.Key);
        Assert.Equal(label, icon.Label);
    }

    /// <summary>The StreamGeometry resources in NightVault.axaml must carry the branding SVGs' path data.</summary>
    [Fact]
    public void Platform_geometries_match_the_branding_svgs()
    {
        var root = PrivatePaste.Tests.Web.PlatformIconsTests.RepoRoot();
        var theme = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "PrivatePaste.Desktop", "Theme", "NightVault.axaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        System.Xml.Linq.XNamespace svgNs = "http://www.w3.org/2000/svg";
        foreach (var icon in PrivatePaste.Desktop.Theme.PlatformIcons.All)
        {
            var geometry = theme.Descendants().Single(e => e.Name.LocalName == "StreamGeometry"
                                                           && (string?)e.Attribute(x + "Key") == icon.ResourceKey);
            var svg = System.Xml.Linq.XDocument.Load(Path.Combine(root, "branding", "platform-icons", icon.Key + ".svg"));
            Assert.Equal((string?)svg.Root!.Element(svgNs + "path")!.Attribute("d"), geometry.Value.Trim());
            Assert.Equal(icon.Label, (string?)svg.Root.Attribute("aria-label"));
        }
    }

    [Fact]
    public void Pin_geometry_matches_the_branding_svg()
    {
        var root = PrivatePaste.Tests.Web.PlatformIconsTests.RepoRoot();
        var theme = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "PrivatePaste.Desktop", "Theme", "NightVault.axaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        System.Xml.Linq.XNamespace svgNs = "http://www.w3.org/2000/svg";
        var geometry = theme.Descendants().Single(e => e.Name.LocalName == "StreamGeometry"
                                                       && (string?)e.Attribute(x + "Key") == "PpIconPin");
        var svg = System.Xml.Linq.XDocument.Load(Path.Combine(root, "branding", "ui-icons", "pin.svg"));
        Assert.Equal((string?)svg.Root!.Element(svgNs + "path")!.Attribute("d"), geometry.Value.Trim());
    }

    private static SignInWindow NewSignIn() => new()
    {
        DataContext = new SignInViewModel((_, _, _, _) => Task.FromResult(new SignInOutcome(SignInResult.Ok, null))),
    };

    private static T Find<T>(Window w, string name) where T : Control =>
        w.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public Task Title_bar_shows_title_and_buttons_with_tooltips() => Session.Value.Dispatch(() =>
    {
        if (!OperatingSystem.IsWindows()) return;   // our title bar is drawn only on Windows (Linux/macOS keep native decorations)
        var w = NewSignIn();
        w.Show();
        try
        {
            var bar = w.GetVisualDescendants().OfType<TitleBar>().Single();
            Assert.Equal("Sign in to PrivatePaste", Find<TextBlock>(w, "TitleText").Text);
            Assert.Equal("Minimize", ToolTip.GetTip(Find<Button>(w, "MinimizeButton")));
            Assert.Equal("Maximize", ToolTip.GetTip(Find<Button>(w, "MaximizeButton")));
            Assert.Equal("Close", ToolTip.GetTip(Find<Button>(w, "CloseButton")));
            Assert.Equal("Close", Avalonia.Automation.AutomationProperties.GetName(Find<Button>(w, "CloseButton")));
            Assert.True(Find<Button>(w, "CloseButton").Focusable);
            Assert.Equal(36, bar.Height);
            Assert.True(w.CanResize);
        }
        finally { w.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Title_bar_minimize_sets_minimized() => Session.Value.Dispatch(() =>
    {
        if (!OperatingSystem.IsWindows()) return;   // our title bar is drawn only on Windows (Linux/macOS keep native decorations)
        var w = NewSignIn();
        w.Show();
        try
        {
            Click(Find<Button>(w, "MinimizeButton"));
            Assert.Equal(WindowState.Minimized, w.WindowState);
        }
        finally { w.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Title_bar_maximize_toggles_state_icon_and_tooltip() => Session.Value.Dispatch(() =>
    {
        if (!OperatingSystem.IsWindows()) return;   // our title bar is drawn only on Windows (Linux/macOS keep native decorations)
        var w = NewSignIn();
        w.Show();
        try
        {
            var max = Find<Button>(w, "MaximizeButton");
            var icon = Find<Avalonia.Controls.Shapes.Path>(w, "MaximizeIcon");
            var before = icon.Data;
            Assert.DoesNotContain("restored", icon.Classes);

            Click(max);
            Assert.Equal(WindowState.Maximized, w.WindowState);
            Assert.Equal("Restore", ToolTip.GetTip(max));
            Assert.Equal("Restore", Avalonia.Automation.AutomationProperties.GetName(max));
            Assert.NotSame(before, icon.Data);
            Assert.Contains("restored", icon.Classes);

            Click(max);
            Assert.Equal(WindowState.Normal, w.WindowState);
            Assert.Equal("Maximize", ToolTip.GetTip(max));
            Assert.DoesNotContain("restored", icon.Classes);
        }
        finally { w.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Title_bar_close_closes_window() => Session.Value.Dispatch(() =>
    {
        if (!OperatingSystem.IsWindows()) return;   // our title bar is drawn only on Windows (Linux/macOS keep native decorations)
        var w = NewSignIn();
        var closed = false;
        w.Closed += (_, _) => closed = true;
        w.Show();
        Click(Find<Button>(w, "CloseButton"));
        Assert.True(closed);
    }, CancellationToken.None);

    [Fact]
    public Task Settings_window_has_title_bar_and_is_resizable() => Session.Value.Dispatch(() =>
    {
        if (!OperatingSystem.IsWindows()) return;   // our title bar is drawn only on Windows (Linux/macOS keep native decorations)
        var w = new SettingsWindow
        {
            DataContext = new SettingsViewModel("http://localhost:5299", "PC-work", "Ctrl+Shift+V", true, "Start with Windows",
                _ => { }, _ => { }, () => Task.CompletedTask),
        };
        w.Show();
        try
        {
            Assert.Equal("PrivatePaste settings", Find<TextBlock>(w, "TitleText").Text);
            Assert.True(w.CanResize);
            Click(Find<Button>(w, "MaximizeButton"));
            Assert.Equal(WindowState.Maximized, w.WindowState);
        }
        finally { w.Close(); }
    }, CancellationToken.None);

    private static void Render(Window window, string theme, string name, Action<Window>? inspect = null)
    {
        window.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        window.Show();
        try
        {
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame!.PixelSize.Width > 100 && frame.PixelSize.Height > 30);
            if (ScreenshotDir is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                frame.Save(Path.Combine(dir, $"{name}-{theme}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            inspect?.Invoke(window);
        }
        finally { window.Close(); }
    }
}
