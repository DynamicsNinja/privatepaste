using System.Text.Json;
using System.Text.Json.Serialization;
using PrivatePaste.Protocol.Crypto;

namespace PrivatePaste.Protocol;

/// <summary>
/// What a clip holds, sealed under the vault key so the server never sees it. Later releases add fields;
/// readers ignore the ones they don't know.
/// </summary>
public sealed record ClipPayload
{
    public string? Text { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    /// <summary>The files of a file clip, in blob order. No <see cref="Text"/> for those.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ClipFileInfo>? Files { get; init; }

    /// <summary>Seals the payload as standard base64 (with padding) for the wire.</summary>
    public static string Seal(VaultKey key, ClipPayload p) =>
        Convert.ToBase64String(SealedBox.Seal(key, "payload", JsonSerializer.SerializeToUtf8Bytes(p, ProtocolJson.Options)));

    public static ClipPayload Open(VaultKey key, string payloadBase64) =>
        JsonSerializer.Deserialize<ClipPayload>(SealedBox.Open(key, "payload", Convert.FromBase64String(payloadBase64)), ProtocolJson.Options)
        ?? throw new JsonException("Payload is null.");
}
