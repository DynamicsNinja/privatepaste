using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
        var vm = new SignInViewModel((_, _, _) => Task.FromResult(new SignInOutcome(SignInResult.Ok, null)))
        {
            ServerUrl = "http://localhost:5299",
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

    [Fact]
    public Task Toast_window_renders() => Session.Value.Dispatch(() =>
    {
        Render(new ToastWindow { Message = "Clip too large to sync (text over 1 MB)." }, "dark", "toast");
    }, CancellationToken.None);

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public Task Popup_window_renders(string theme) => Session.Value.Dispatch(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new PrivatePaste.Client.Core.Cache.HistoryCache(
            Path.Combine(Path.GetTempPath(), "pp-render-" + Guid.NewGuid() + ".dat"), new byte[32]);
        PrivatePaste.Protocol.ClipDto Clip(int seq, PrivatePaste.Protocol.ClipKind kind, string? text, int minutes, string device, int? w = null, int? h = null) =>
            new(Guid.NewGuid(), seq, kind, text, "h" + seq, w, h, 100, null, device, now.AddMinutes(-minutes));
        cache.Upsert(Clip(5, PrivatePaste.Protocol.ClipKind.Text, "git commit -m \"fix(client): sync engine resume\"\nsecond line", 0, "PC-work"));
        cache.Upsert(Clip(4, PrivatePaste.Protocol.ClipKind.Link, "https://paste.example.com/some/long/path?with=query&and=more", 4, "MacBook"));
        cache.Upsert(Clip(3, PrivatePaste.Protocol.ClipKind.Image, null, 35, "PC-work", 1280, 720));
        cache.Upsert(Clip(2, PrivatePaste.Protocol.ClipKind.Text, new string('x', 200), 190, "Phone"));
        cache.Upsert(Clip(1, PrivatePaste.Protocol.ClipKind.Text, "Meeting notes", 3000, "PC-work"));
        var vm = new PopupViewModel(cache, (_, _) => Task.FromResult<IReadOnlyList<PrivatePaste.Protocol.ClipDto>>([]),
            _ => Task.CompletedTask, TimeProvider.System);
        vm.MoveSelection(1);
        var window = new PopupWindow { DataContext = vm };
        Render(window, theme, "popup");
        Assert.True(vm.Items[0].IsNewest);
    }, CancellationToken.None);

    private static void Render(Window window, string theme, string name)
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
        }
        finally { window.Close(); }
    }
}
