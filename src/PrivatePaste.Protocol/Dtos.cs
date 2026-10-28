namespace PrivatePaste.Protocol;

public enum ClipKind { Text, Link, Image }

public sealed record ClipDto(Guid Id, long Seq, ClipKind Kind, string? Text, string Hash,
    int? Width, int? Height, long SizeBytes, Guid? SourceDeviceId, string SourceDeviceName,
    DateTimeOffset LastCopiedAt);

public sealed record LoginRequest(string Password, string DeviceName, string Platform);

public sealed record LoginResponse(Guid DeviceId, string Token);

public sealed record TextClipUpload(string Text, DateTimeOffset? CopiedAt, bool Backfill);

public sealed record ClipPage(IReadOnlyList<ClipDto> Items, string? NextCursor);

public sealed record SyncResponse(IReadOnlyList<ClipDto> Clips, IReadOnlyList<Guid> DeletedIds, long LatestSeq, bool HasMore);

public sealed record DeviceDto(Guid Id, string Name, string Platform, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool IsCurrent);

/// <summary>WebSocket envelope. <see cref="Type"/> is one of <see cref="WsTypes"/>.</summary>
public sealed record WsMessage(string Type, ClipDto? Clip = null, Guid? Id = null, long? Seq = null, bool? Live = null);

public static class WsTypes
{
    public const string ClipCreated = "clip.created";
    public const string ClipDeleted = "clip.deleted";
    public const string Ping = "ping";
    public const string Pong = "pong";
}
