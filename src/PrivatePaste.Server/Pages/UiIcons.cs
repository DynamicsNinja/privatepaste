using Microsoft.AspNetCore.Html;

namespace PrivatePaste.Server.Pages;

/// <summary>
/// Interface icons from <c>branding/ui-icons</c>, copied here for the same reason as <see cref="PlatformIcons"/>;
/// a test keeps the path data in sync with the SVG file.
/// </summary>
public static class UiIcons
{
    public const string PinPath = "M12 17v5M9 3h6l-1 6 3 3v2H7v-2l3-3z";

    /// <summary>The pin as inline SVG in the surrounding text colour; decorative, so the caller supplies the label.</summary>
    public static IHtmlContent Pin(string cssClass) => new HtmlString(
        $"<svg class=\"{cssClass}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" " +
        $"stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"{PinPath}\"/></svg>");
}
