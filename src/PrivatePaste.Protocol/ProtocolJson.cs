using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivatePaste.Protocol;

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new ClipKindConverter());
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}

/// <summary>Reads a kind this build doesn't know as <see cref="ClipKind.Unknown"/> instead of failing the whole message.</summary>
internal sealed class ClipKindConverter : JsonConverter<ClipKind>
{
    public override ClipKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "text" => ClipKind.Text,
                "link" => ClipKind.Link,
                "image" => ClipKind.Image,
                _ => ClipKind.Unknown,
            }
            : ClipKind.Unknown;

    public override void Write(Utf8JsonWriter writer, ClipKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ClipKind.Text => "text",
            ClipKind.Link => "link",
            ClipKind.Image => "image",
            _ => throw new ArgumentOutOfRangeException(nameof(value), "Unknown is never written."),
        });
}
