namespace PrivatePaste.Desktop.Theme;

/// <summary>A device platform's icon: its accessible label and the StreamGeometry resource in NightVault.axaml.</summary>
public sealed record PlatformIcon(string Key, string Label)
{
    /// <summary>Resource key of the 24x24 stroked geometry, converted from <c>branding/platform-icons/{Key}.svg</c>.</summary>
    public string ResourceKey => "PpPlatform" + char.ToUpperInvariant(Key[0]) + Key[1..];
}

public static class PlatformIcons
{
    public static readonly PlatformIcon Windows = new("windows", "Windows");
    public static readonly PlatformIcon MacOs = new("macos", "macOS");
    public static readonly PlatformIcon Linux = new("linux", "Linux");
    public static readonly PlatformIcon Android = new("android", "Android");
    public static readonly PlatformIcon Web = new("web", "Web");
    public static readonly PlatformIcon Unknown = new("unknown", "Unknown device");

    public static IReadOnlyList<PlatformIcon> All { get; } = [Windows, MacOs, Linux, Android, Web, Unknown];

    /// <summary>The icon for a clip's source platform; anything unrecognized (or null) is <see cref="Unknown"/>.</summary>
    public static PlatformIcon For(string? platform) => platform?.Trim().ToLowerInvariant() switch
    {
        "windows" => Windows,
        "macos" => MacOs,
        "linux" => Linux,
        "android" => Android,
        "web" => Web,
        _ => Unknown,
    };
}
