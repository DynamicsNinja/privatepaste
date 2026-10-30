using System.Text.Json.Serialization;

namespace PrivatePaste.Protocol;

public enum ClipKind { Text, Link, Image }

public sealed record ClipDto(Guid Id, long Seq, ClipKind Kind, string? Text, string Hash,
    int? Width, int? Height, long SizeBytes, Guid? SourceDeviceId, string SourceDeviceName,
    [property: JsonPropertyOrder(1)] DateTimeOffset LastCopiedAt)
{
    /// <summary>
    /// Lowercase platform of the device that last copied the clip ("windows", "macos", "linux", "android", "web", ...),
    /// or null when unknown (e.g. a clip from a revoked device that predates platform tracking).
    /// Serialized right after <see cref="SourceDeviceName"/>.
    /// </summary>
    public string? SourceDevicePlatform { get; init; }

    /// <summary>When the clip was pinned, or null when it is not pinned. Pinned clips survive history clearing.</summary>
    public DateTimeOffset? PinnedAt { get; init; }
}

public sealed record LoginRequest(string Password, string DeviceName, string Platform)
{
    /// <summary>The person signing in. Apps from before there were people send none and are told to update.</summary>
    public string? Username { get; init; }
}

public sealed record LoginResponse(Guid DeviceId, string Token);

public sealed record TextClipUpload(string Text, DateTimeOffset? CopiedAt, bool Backfill);

public sealed record ClipPage(IReadOnlyList<ClipDto> Items, string? NextCursor);

public sealed record SyncResponse(IReadOnlyList<ClipDto> Clips, IReadOnlyList<Guid> DeletedIds, long LatestSeq, bool HasMore);

public sealed record DeviceDto(Guid Id, string Name, string Platform, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool IsCurrent);

/// <summary>Server settings. <see cref="AutoDeleteDays"/> is null (off), 7, 30 or 90.</summary>
public sealed record SettingsDto(int? AutoDeleteDays);

/// <summary>WebSocket envelope. <see cref="Type"/> is one of <see cref="WsTypes"/>.</summary>
public sealed record WsMessage(string Type, ClipDto? Clip = null, Guid? Id = null, long? Seq = null, bool? Live = null);

public static class WsTypes
{
    public const string ClipCreated = "clip.created";
    public const string ClipDeleted = "clip.deleted";
    public const string Ping = "ping";
    public const string Pong = "pong";
}
