using System.Text.Json;
using System.Text;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;

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

    [Fact]
    public void UnknownKind_ParsesAsUnknown()
    {
        var id = Guid.NewGuid();
        var json = $$"""{"id":"{{id}}","seq":5,"kind":"rich","text":"hi","hash":"h","sizeBytes":2,"sourceDeviceName":"pc","lastCopiedAt":"1970-01-01T00:00:00+00:00"}""";
        var clip = JsonSerializer.Deserialize<ClipDto>(json, ProtocolJson.Options)!;
        Assert.Equal(ClipKind.Unknown, clip.Kind);
        Assert.Equal(id, clip.Id);
        Assert.Equal(5, clip.Seq);
        Assert.Equal("hi", clip.Text);
        Assert.Equal("pc", clip.SourceDeviceName);
    }

    [Fact]
    public void ClipDto_NewFieldsRoundTrip()
    {
        var clip = new ClipDto(Guid.NewGuid(), 1, ClipKind.Image, null, "h", 3, 4, 9, null, "pc", DateTimeOffset.UnixEpoch)
        { Payload = "AAEC", KeyId = "0011223344556677", HasThumb = true, BlobCount = 2 };
        var json = JsonSerializer.Serialize(clip, ProtocolJson.Options);
        Assert.Contains("\"payload\":\"AAEC\"", json);
        var back = JsonSerializer.Deserialize<ClipDto>(json, ProtocolJson.Options)!;
        Assert.Equal("AAEC", back.Payload);
        Assert.Equal("0011223344556677", back.KeyId);
        Assert.True(back.HasThumb);
        Assert.Equal(2, back.BlobCount);
    }

    [Fact]
    public void Payload_RoundTripsAndIgnoresUnknownFields()
    {
        using var key = VaultKey.Create();
        var rt = ClipPayload.Open(key, ClipPayload.Seal(key, new ClipPayload { Text = "a", Width = 1, Height = 2 }));
        Assert.Equal(("a", 1, 2), (rt.Text, rt.Width, rt.Height));

        var raw = Encoding.UTF8.GetBytes("""{"text":"a","html":"<b>a</b>"}""");
        var sealedB64 = Convert.ToBase64String(SealedBox.Seal(key, "payload", raw));
        Assert.Equal("a", ClipPayload.Open(key, sealedB64).Text);
    }

    [Fact]
    public void File_kind_round_trips()
    {
        Assert.Contains("\"kind\":\"file\"", JsonSerializer.Serialize(SampleClip(ClipKind.File), ProtocolJson.Options));
        var json = $$"""{"id":"{{Guid.NewGuid()}}","seq":1,"kind":"file","hash":"h","sizeBytes":2,"sourceDeviceName":"pc","lastCopiedAt":"1970-01-01T00:00:00+00:00"}""";
        Assert.Equal(ClipKind.File, JsonSerializer.Deserialize<ClipDto>(json, ProtocolJson.Options)!.Kind);
    }

    [Fact]
    public void Missing_fileMb_defaults_to_25()
    {
        var info = JsonSerializer.Deserialize<ServerInfoDto>(
            """{"protocol":2,"kinds":["text"],"limits":{"textKb":256,"imageMb":10}}""", ProtocolJson.Options)!;
        Assert.Equal(25, info.Limits.FileMb);
        var set = JsonSerializer.Deserialize<ServerLimitsDto>("""{"textKb":1,"imageMb":2,"fileMb":7}""", ProtocolJson.Options)!;
        Assert.Equal(7, set.FileMb);
    }

    [Fact]
    public void Clip_files_field_is_optional()
    {
        var json = $$"""{"id":"{{Guid.NewGuid()}}","seq":1,"kind":"file","hash":"h","sizeBytes":2,"sourceDeviceName":"pc","lastCopiedAt":"1970-01-01T00:00:00+00:00"}""";
        Assert.Null(JsonSerializer.Deserialize<ClipDto>(json, ProtocolJson.Options)!.Files);
        var withFiles = SampleClip(ClipKind.File) with { Files = [new ClipFileInfo("a.txt", 5, "text/plain")] };
        var back = JsonSerializer.Deserialize<ClipDto>(JsonSerializer.Serialize(withFiles, ProtocolJson.Options), ProtocolJson.Options)!;
        Assert.Equal(new ClipFileInfo("a.txt", 5, "text/plain"), Assert.Single(back.Files!));
    }

    [Fact]
    public void Payload_files_use_the_wire_field_names()
    {
        using var key = VaultKey.Create();
        var sealedB64 = ClipPayload.Seal(key, new ClipPayload { Files = [new ClipFileInfo("a.txt", 5, "text/plain")] });
        var json = Encoding.UTF8.GetString(SealedBox.Open(key, "payload", Convert.FromBase64String(sealedB64)));
        // text, width and height are written as null today and readers ignore them; the files part is the contract here.
        Assert.Contains("\"files\":[{\"name\":\"a.txt\",\"size\":5,\"mime\":\"text/plain\"}]", json);
    }

    [Fact]
    public void Payload_files_round_trip_and_are_omitted_when_null()
    {
        using var key = VaultKey.Create();
        var files = new[] { new ClipFileInfo("a.txt", 5, "text/plain"), new ClipFileInfo("b", 0, "application/octet-stream") };
        var rt = ClipPayload.Open(key, ClipPayload.Seal(key, new ClipPayload { Files = files }));
        Assert.Equal(files, rt.Files);
        Assert.Null(rt.Text);
        Assert.DoesNotContain("files", JsonSerializer.Serialize(new ClipPayload { Text = "a" }, ProtocolJson.Options));
    }
}
