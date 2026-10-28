using System.Text.Json;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Protocol;

public class ProtocolJsonTests
{
    private static ClipDto SampleClip(ClipKind kind) => new(
        Guid.NewGuid(), 7, kind, "https://x.io", "abc", null, null, 12, Guid.NewGuid(), "laptop",
        DateTimeOffset.UnixEpoch);

    [Fact]
    public void Kind_serializes_lowercase() =>
        Assert.Contains("\"kind\":\"link\"", JsonSerializer.Serialize(SampleClip(ClipKind.Link), ProtocolJson.Options));

    [Fact]
    public void WsMessage_roundtrips()
    {
        var clip = SampleClip(ClipKind.Text);
        var json = JsonSerializer.Serialize(new WsMessage(WsTypes.ClipCreated, clip, Live: true), ProtocolJson.Options);
        var back = JsonSerializer.Deserialize<WsMessage>(json, ProtocolJson.Options)!;
        Assert.Equal(WsTypes.ClipCreated, back.Type);
        Assert.Equal(clip.Id, back.Clip!.Id);
        Assert.True(back.Live);
        Assert.Contains("\"type\":\"clip.created\"", json);
    }
}
