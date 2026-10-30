using PrivatePaste.Desktop.Theme;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop.Tray;

/// <summary>What the "clip arrived" toast shows: the source platform icon, "From {device}" (or "From {person} · {device}") and a one-line preview.</summary>
public sealed record ClipToast(PlatformIcon Platform, string Title, string Preview)
{
    public const int PreviewLength = 80;

    public static ClipToast For(ClipDto clip)
    {
        var device = clip.SourceDeviceName?.Trim();
        var person = clip.SourceUserName?.Trim();
        var title = (person, device) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"From {person} · {device}",
            ({ Length: > 0 }, _) => $"From {person}",
            (_, { Length: > 0 }) => $"From {device}",
            _ => "From another device",
        };
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
        if (line.Length <= PreviewLength) return line;
        var cut = PreviewLength - 1;
        if (char.IsHighSurrogate(line[cut - 1])) cut--;   // never split an emoji (surrogate pair)
        return line[..cut] + "…";
    }
}
