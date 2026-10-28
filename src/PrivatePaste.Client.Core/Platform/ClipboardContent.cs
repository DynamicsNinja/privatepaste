namespace PrivatePaste.Client.Core.Platform;

public abstract record ClipboardContent
{
    public sealed record Text(string Value) : ClipboardContent;

    public sealed record Image(byte[] Png) : ClipboardContent;
}
