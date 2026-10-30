using Microsoft.AspNetCore.Html;

namespace PrivatePaste.Server.Pages;

/// <summary>
/// Interface icons from <c>branding/ui-icons</c>, copied here for the same reason as <see cref="PlatformIcons"/>;
/// tests keep the path data in sync with the SVG files.
/// </summary>
public static class UiIcons
{
    public const string PinPath = "M12 17v5M9 3h6l-1 6 3 3v2H7v-2l3-3z";
    public const string CopyPath = "M9 9h10a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V10a1 1 0 0 1 1-1zM5 15H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v1";
    public const string TrashPath = "M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3";
    public const string CheckPath = "M5 12.5l4.5 4.5L19 7.5";

    // Decorative inline SVGs in the surrounding text colour; the caller supplies the label.
    public static IHtmlContent Pin(string cssClass) => Svg(PinPath, cssClass);

    public static IHtmlContent Copy(string cssClass) => Svg(CopyPath, cssClass);

    public static IHtmlContent Trash(string cssClass) => Svg(TrashPath, cssClass);

    public static IHtmlContent Check(string cssClass) => Svg(CheckPath, cssClass);

    private static HtmlString Svg(string path, string cssClass) => new(
        $"<svg class=\"{cssClass}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" " +
        $"stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"{path}\"/></svg>");
}
