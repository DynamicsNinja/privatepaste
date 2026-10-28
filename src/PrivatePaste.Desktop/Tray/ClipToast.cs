using PrivatePaste.Desktop.Theme;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop.Tray;

/// <summary>What the "clip arrived" toast shows: the source platform icon, "From {device}" and a one-line preview.</summary>
public sealed record ClipToast(PlatformIcon Platform, string Title, string Preview)
{
    public const int PreviewLength = 80;

    public static ClipToast For(ClipDto clip)
    {
        var device = clip.SourceDeviceName?.Trim();
        var title = string.IsNullOrEmpty(device) ? "From another device" : $"From {device}";
        return new ClipToast(PlatformIcons.For(clip.SourceDevicePlatform), title, FormatPreview(clip));
    }

    /// <summary>"Image {w}×{h}", or the first non-empty line of the text, cut to <see cref="PreviewLength"/> chars with "…".</summary>
    public static string FormatPreview(ClipDto clip)
    {
        if (clip.Kind == ClipKind.Image)
            return clip is { Width: { } w, Height: { } h } ? $"Image {w}×{h}" : "Image";
        var line = (clip.Text ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= PreviewLength ? line : line[..(PreviewLength - 1)] + "…";
    }
}
