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

    private static PrivatePaste.Protocol.ClipDto FileClip(int seq, int minutes, string device, string platform,
        params (string Name, long Size)[] files) =>
        new(Guid.NewGuid(), seq, PrivatePaste.Protocol.ClipKind.File, null, "f" + seq, null, null, files.Sum(f => f.Size), null, device,
            DateTimeOffset.UtcNow.AddMinutes(-minutes))
        {
            SourceDevicePlatform = platform,
            Files = files.Select(f => new PrivatePaste.Protocol.ClipFileInfo(f.Name, f.Size, "application/pdf")).ToList(),
            BlobCount = files.Length,
        };

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task File_toast_renders(string theme) => Session.Value.Dispatch(() =>
    {
        var toast = PrivatePaste.Desktop.Tray.ClipToast.For(
            FileClip(1, 0, "Pixel 9", "android", ("report.pdf", 3_000_000), ("a.txt", 300_000), ("b.txt", 265_000)));
        Assert.Equal("report.pdf +2 more · 3.4 MB", toast.Preview);
        var window = new ToastWindow();
        window.ShowClip(toast);
        Render(window, theme, "toast-file", w =>
        {
            Assert.Equal("From Pixel 9", Find<TextBlock>(w, "ClipTitle").Text);
            Assert.Equal("report.pdf +2 more · 3.4 MB", Find<TextBlock>(w, "ClipPreview").Text);
        });
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Popup_window_renders_a_file_row(string theme) => Session.Value.Dispatch(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PrivatePaste.Client.Core.Cache.HistoryCache(
            Path.Combine(Path.GetTempPath(), "pp-render-" + Guid.NewGuid() + ".dat"), new byte[32]);
        cache.Upsert(new PrivatePaste.Protocol.ClipDto(Guid.NewGuid(), 4, PrivatePaste.Protocol.ClipKind.Text,
            "git commit -m \"feat(desktop): copy and paste files\"", "h4", null, null, 100, null, "PC-work", now)
        { SourceDevicePlatform = "windows" });
        cache.Upsert(FileClip(3, 4, "Pixel 9", "android", ("report.pdf", 3_000_000), ("a.txt", 300_000), ("b.txt", 265_000)));
        cache.Upsert(FileClip(2, 35, "MacBook", "macos", ("Quarterly budget with a very long file name for the trimming check.xlsx", 48_213)));
        cache.Upsert(new PrivatePaste.Protocol.ClipDto(Guid.NewGuid(), 1, PrivatePaste.Protocol.ClipKind.Image, null, "h1", 1280, 720,
            100, null, "ThinkPad", now.AddMinutes(-90)) { SourceDevicePlatform = "linux" });
        var vm = new PopupViewModel(cache, null, _ => Task.CompletedTask, TimeProvider.System,
            save: _ => Task.FromResult<string?>(null));
        vm.MoveSelection(1);   // the file row: the footer offers Save
        Render(new PopupWindow { DataContext = vm }, theme, "popup-files", w =>
        {
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("report.pdf +2 more · 3.4 MB", texts);
            Assert.Contains("Enter to paste   Tab for Pinned   Ctrl+P to pin   Ctrl+S to save   Ctrl+1–5 to filter", texts);
            var fileIcons = w.GetVisualDescendants().OfType<Canvas>().Where(c => c.Name == "FileIcon" && c.IsEffectivelyVisible).ToList();
            Assert.Equal(2, fileIcons.Count);
            Assert.All(w.GetVisualDescendants().OfType<Button>(), b => Assert.False(b.Focusable));
        });
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark", PrivatePaste.Desktop.ViewModels.PopupFilter.All)]
    [InlineData("light", PrivatePaste.Desktop.ViewModels.PopupFilter.All)]
    [InlineData("dark", PrivatePaste.Desktop.ViewModels.PopupFilter.File)]
    [InlineData("light", PrivatePaste.Desktop.ViewModels.PopupFilter.File)]
    [InlineData("dark", PrivatePaste.Desktop.ViewModels.PopupFilter.Image)]
    public Task Popup_window_renders_the_type_chips(string theme, PrivatePaste.Desktop.ViewModels.PopupFilter filter) => Session.Value.Dispatch(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PrivatePaste.Client.Core.Cache.HistoryCache(
            Path.Combine(Path.GetTempPath(), "pp-render-" + Guid.NewGuid() + ".dat"), new byte[32]);
        cache.Upsert(new PrivatePaste.Protocol.ClipDto(Guid.NewGuid(), 4, PrivatePaste.Protocol.ClipKind.Text,
            "git commit -m \"feat(desktop): filter by type\"", "h4", null, null, 100, null, "PC-work", now)
        { SourceDevicePlatform = "windows" });
        cache.Upsert(FileClip(3, 4, "Pixel 9", "android", ("report.pdf", 3_000_000), ("a.txt", 300_000)));
        cache.Upsert(FileClip(2, 35, "MacBook", "macos", ("budget.xlsx", 48_213)));
        var vm = new PopupViewModel(cache, null, _ => Task.CompletedTask, TimeProvider.System,
            save: _ => Task.FromResult<string?>(null));
        vm.SetFilter(filter);
        vm.MoveSelection(0);
        Render(new PopupWindow { DataContext = vm }, theme, $"popup-chips-{filter.ToString().ToLowerInvariant()}", w =>
        {
            var chips = w.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip")).ToList();
            Assert.Equal(["FilterAllChip", "FilterTextChip", "FilterLinkChip", "FilterImageChip", "FilterFileChip"], chips.Select(c => c.Name));
            Assert.All(chips, c => Assert.False(c.Focusable));
            // Exactly the chosen chip is marked active.
            Assert.Equal([$"Filter{filter}Chip"], chips.Where(c => c.Classes.Contains("active")).Select(c => c.Name));
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Subset(texts.ToHashSet(), new HashSet<string?> { "All", "Text", "Links", "Images", "Files" });
            // The footer fits in the popup: one line (or two when a file row adds Ctrl+S) never clips.
            var footer = Find<TextBlock>(w, "FooterText");
            Assert.True(footer.Bounds.Height < 40, $"footer is {footer.Bounds.Height}px high");
            Assert.Equal(vm.Items.Count, filter switch
            {
                PrivatePaste.Desktop.ViewModels.PopupFilter.File => 2,
                PrivatePaste.Desktop.ViewModels.PopupFilter.Image => 0,
                _ => 3,
            });
        });
    }, CancellationToken.None);

    [Fact]
    public void File_icon_matches_the_branding_svg()
    {
        var root = PrivatePaste.Tests.Web.PlatformIconsTests.RepoRoot();
        var svg = System.Xml.Linq.XDocument.Load(Path.Combine(root, "branding", "ui-icons", "kind-file.svg"));
        var d = svg.Descendants().Single(e => e.Name.LocalName == "path").Attribute("d")!.Value;
        var axaml = File.ReadAllText(Path.Combine(root, "src", "PrivatePaste.Desktop", "Views", "PopupWindow.axaml"));
        Assert.Contains($"Data=\"{d}\"", axaml);
    }

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
        var vm = new PopupViewModel(cache, null,
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
        var vm = new PopupViewModel(cache, null,
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
        var vm = new PopupViewModel(cache, null,
            _ => Task.CompletedTask, TimeProvider.System);
        Render(new PopupWindow { DataContext = vm }, theme, "popup-people", w =>
        {
            var chips = w.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PersonChip" && b.IsEffectivelyVisible).ToList();
            Assert.Equal(["Ana", "Marko", "Ana"], chips.Select(c => ((TextBlock)c.Child!).Text));
            Assert.All(chips, c => Assert.False(c.Focusable));
        });
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark", UnlockMode.Unlock)]
    [InlineData("light", UnlockMode.Unlock)]
    [InlineData("dark", UnlockMode.Setup)]
    [InlineData("light", UnlockMode.Setup)]
    public Task Unlock_window_renders(string theme, UnlockMode mode) => Session.Value.Dispatch(() =>
    {
        var vm = new UnlockViewModel(_ => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), mode,
            mode == UnlockMode.Unlock ? VaultCopy.WasReset : null)
        {
            Passphrase = "my cat eats lemons daily",
            Repeat = mode == UnlockMode.Setup ? "my cat eats lemons" : "",
            Error = mode == UnlockMode.Unlock ? VaultCopy.WrongPassphrase : VaultCopy.Mismatch,
        };
        Render(new UnlockWindow { DataContext = vm }, theme, mode == UnlockMode.Unlock ? "unlock" : "setup", w =>
        {
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(mode == UnlockMode.Unlock ? "Unlock your history" : "Set up encryption", texts);
            Assert.Equal(mode == UnlockMode.Setup, texts.Contains("Use at least 12 characters. A short sentence works well."));
            Assert.Equal(mode == UnlockMode.Setup, Find<TextBox>(w, "RepeatBox").IsEffectivelyVisible);
            Assert.Equal(mode == UnlockMode.Unlock, texts.Contains("Encryption was reset. Enter the new passphrase."));
            Assert.Equal(mode == UnlockMode.Unlock ? "Unlock" : "Set up", Find<Button>(w, "SubmitButton").Content);
            Assert.Equal('•', Find<TextBox>(w, "PassphraseBox").PasswordChar);
            Assert.True(Find<TextBox>(w, "PassphraseBox").Focusable);   // an ordinary window: typing needs the focus
        });
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark", false)]
    [InlineData("light", false)]
    [InlineData("dark", true)]
    [InlineData("light", true)]
    public Task Add_device_window_renders(string theme, bool expired) => Session.Value.Dispatch(() =>
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var vm = new AddDeviceViewModel(_ => Task.FromResult((
            new PrivatePaste.Protocol.HandoverCreated(Guid.NewGuid(), time.GetUtcNow().AddMinutes(10)),
            "privatepaste:handover?id=0b7e6c1a-3f0e-4a8e-9b1e-1a2b3c4d5e6f&k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&kid=0123456789abcdef")),
            time);
        vm.ShowNewCodeCommand.Execute(null);   // completes at once
        time.Advance(TimeSpan.FromSeconds(expired ? 601 : 13));
        vm.Tick();
        Render(new AddDeviceWindow { DataContext = vm }, theme, expired ? "add-device-expired" : "add-device", w =>
        {
            Assert.Equal(!expired, Find<Border>(w, "QrCard").IsEffectivelyVisible);
            Assert.Equal(expired, Find<StackPanel>(w, "ProblemPanel").IsEffectivelyVisible);
            if (expired) Assert.Equal("That code has expired. Show a new one.", Find<TextBlock>(w, "ProblemText").Text);
            else
            {
                Assert.Equal("9:47 left", Find<TextBlock>(w, "TimeLeftText").Text);
                Assert.NotNull(Find<Image>(w, "QrImage").Source);
            }
        });
    }, CancellationToken.None);

    [Fact]
    public void Qr_png_has_one_pixel_per_module()
    {
        var png = AddDeviceWindow.QrPng("privatepaste:handover?id=x&k=y&kid=z");
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal(bitmap.Width, bitmap.Height);
        Assert.True(bitmap.Width is >= 21 and < 80, $"{bitmap.Width} modules");
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Settings_window_renders_encryption(string theme) => Session.Value.Dispatch(() =>
    {
        var vm = new SettingsViewModel("http://localhost:5299", "PC-work", "Ctrl+Shift+V", true, "Start with Windows",
            _ => { }, _ => { }, () => Task.CompletedTask, isOwner: true,
            changePassphrase: (_, _, _) => Task.FromResult<string?>(null), addDevice: () => { });
        vm.StartChangePassphraseCommand.Execute(null);
        vm.PassphraseError = "The current passphrase is wrong.";
        Render(new SettingsWindow { DataContext = vm }, theme, "settings-encryption", w =>
        {
            Assert.True(Find<Border>(w, "EncryptionPanel").IsEffectivelyVisible);
            Assert.True(Find<Button>(w, "AddDeviceButton").IsEffectivelyVisible);
            Assert.True(Find<StackPanel>(w, "ChangePassphrasePanel").IsEffectivelyVisible);
        });
    }, CancellationToken.None);

    private static async IAsyncEnumerable<PrivatePaste.Protocol.ClipDto> NoOlderClips(string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Popup_window_renders_unknown_rows_search_older_and_banner(string theme) => Session.Value.Dispatch(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PrivatePaste.Client.Core.Cache.HistoryCache(
            Path.Combine(Path.GetTempPath(), "pp-render-" + Guid.NewGuid() + ".dat"), new byte[32]);
        cache.Upsert(new PrivatePaste.Protocol.ClipDto(Guid.NewGuid(), 2, PrivatePaste.Protocol.ClipKind.Unknown, null, "h2", null, null,
            100, null, "Pixel 9", now.AddMinutes(-3)) { SourceDevicePlatform = "android" });
        cache.Upsert(new PrivatePaste.Protocol.ClipDto(Guid.NewGuid(), 1, PrivatePaste.Protocol.ClipKind.Text, "Meeting notes", "h1", null,
            null, 100, null, "PC-work", now.AddMinutes(-9)) { SourceDevicePlatform = "windows" });
        var vm = new PopupViewModel(cache, NoOlderClips, _ => Task.CompletedTask, TimeProvider.System)
        {
            Banner = VaultCopy.UpdateRequired,
        };
        Render(new PopupWindow { DataContext = vm }, theme, "popup-unknown", w =>
        {
            Assert.True(Find<Border>(w, "BannerPanel").IsEffectivelyVisible);
            Assert.Equal("Update PrivatePaste to keep syncing.", Find<TextBlock>(w, "BannerText").Text);
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Update PrivatePaste to see this clip", texts);
            Assert.All(w.GetVisualDescendants().OfType<Button>(), b => Assert.False(b.Focusable));
        });

        var searching = new PopupViewModel(cache, NoOlderClips, _ => Task.CompletedTask, TimeProvider.System) { Query = "invoice" };
        Render(new PopupWindow { DataContext = searching }, theme, "popup-search-older", w =>
        {
            Assert.True(Assert.Single(searching.Items).IsSearchOlder);
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Search older clips", texts);
            Assert.False(Find<Border>(w, "BannerPanel").IsEffectivelyVisible);
            // The search row has no device line and no pin button.
            Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("pin") && b.IsEffectivelyVisible);
        });
    }, CancellationToken.None);

    /// <summary>
    /// The README hero's popup, from the demo history in scripts/readme/demo.json (see scripts/readme/README.md).
    /// Runs only when PP_README_DEMO names that file; saves readme-popup-{theme}.png into PP_SCREENSHOT_DIR at 2x
    /// (840x960), so the homepage can show it sharp on high-density screens.
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
        var vm = new PopupViewModel(cache, null,
            _ => Task.CompletedTask, TimeProvider.System);
        vm.MoveSelection(1);
        Render(new PopupWindow { DataContext = vm }, theme, "readme-popup", scale: 2);
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

    /// <param name="scale">Pixel density of the saved PNG (the window is laid out at its normal size, then drawn scaled).</param>
    private static void Render(Window window, string theme, string name, Action<Window>? inspect = null, int scale = 1)
    {
        window.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        if (scale != 1)
        {
            // The headless window renders at 1x, so draw its content scaled up in a window that much larger: text and
            // shapes stay sharp, unlike an upscaled 1x picture. (A RenderTargetBitmap at a higher DPI drops the content of
            // a row with a box shadow.)
            var content = (Control)window.Content!;
            window.Content = null;
            window.Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = content };
            window.Width *= scale;
            window.Height *= scale;
        }
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
