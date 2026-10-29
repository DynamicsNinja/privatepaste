namespace PrivatePaste.Client.Core.Platform;

public abstract record ClipboardContent
{
    /// <summary>The copying app marked this copy as private (e.g. a password manager): it must never leave the device.</summary>
    public bool IsPrivate { get; init; }

    /// <summary>
    /// The app the copy came from: its executable or bundle name, without path or extension, lowercase; null when unknown.
    /// </summary>
    public string? SourceApp { get; init; }

    public sealed record Text(string Value) : ClipboardContent;

    public sealed record Image(byte[] Png) : ClipboardContent;
}
