using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace PrivatePaste.Server.Pages;

/// <summary>A device platform's icon: a single 24x24 stroked path (stroke 2, round caps and joins).</summary>
public sealed record PlatformIcon(string Key, string Label, string PathData);

/// <summary>
/// The platform icons from <c>branding/platform-icons</c>, copied here because the server image is built from
/// <c>src/</c> only. A test keeps the path data in sync with the SVG files.
/// </summary>
public static class PlatformIcons
{
    public static readonly PlatformIcon Windows = new("windows", "Windows",
        "M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z");

    public static readonly PlatformIcon MacOs = new("macos", "macOS",
        "M9 9h6v6H9zM9 9V6a3 3 0 1 0-3 3h3M15 9V6a3 3 0 1 1 3 3h-3M15 15v3a3 3 0 1 0 3-3h-3M9 15v3a3 3 0 1 1-3-3h3");

    public static readonly PlatformIcon Linux = new("linux", "Linux",
        "M4 6l6 6-6 6M12 18h8");

    public static readonly PlatformIcon Android = new("android", "Android",
        "M3 18.5a9 9 0 0 1 18 0zM7.5 10.7 5.5 7.2M16.5 10.7 18.5 7.2M8.5 14.5h.01M15.5 14.5h.01");

    public static readonly PlatformIcon Web = new("web", "Web",
        "M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0zM3 12h18M12 3c-2.5 2.4-3.8 5.4-3.8 9s1.3 6.6 3.8 9M12 3c2.5 2.4 3.8 5.4 3.8 9s-1.3 6.6-3.8 9");

    public static readonly PlatformIcon Unknown = new("unknown", "Unknown device",
        "M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM12 16v4M8 20h8");

    public static IReadOnlyList<PlatformIcon> All { get; } = [Windows, MacOs, Linux, Android, Web, Unknown];

    /// <summary>The icon for a device platform; anything unrecognized (or null) is <see cref="Unknown"/>.</summary>
    public static PlatformIcon For(string? platform) => platform?.Trim().ToLowerInvariant() switch
    {
        "windows" => Windows,
        "macos" => MacOs,
        "linux" => Linux,
        "android" => Android,
        "web" => Web,
        _ => Unknown,
    };

    /// <summary>
    /// The icon as inline SVG, so it takes the surrounding text colour through currentColor and needs no
    /// cache-busted asset URL. Sized and coloured by the <c>.platform</c> rule in site.css.
    /// </summary>
    public static IHtmlContent Svg(string? platform)
    {
        var icon = For(platform);
        var label = HtmlEncoder.Default.Encode(icon.Label);
        return new HtmlString(
            $"<svg class=\"platform\" data-platform=\"{icon.Key}\" width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" " +
            $"stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" role=\"img\" aria-label=\"{label}\">" +
            $"<title>{label}</title><path d=\"{icon.PathData}\"/></svg>");
    }
}
