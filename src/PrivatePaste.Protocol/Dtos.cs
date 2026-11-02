using System.Text.Json.Serialization;

namespace PrivatePaste.Protocol;

/// <summary>
/// <see cref="Unknown"/> is what a reader makes of a kind it doesn't know (a newer client's); no client or server ever writes it.
/// </summary>
public enum ClipKind { Text, Link, Image, File, Unknown }

/// <summary>One file of a file clip, as listed inside the sealed payload.</summary>
public sealed record ClipFileInfo(string Name, long Size, string Mime);

public sealed record ClipDto(Guid Id, long Seq, ClipKind Kind, string? Text, string Hash,
    int? Width, int? Height, long SizeBytes, Guid? SourceDeviceId, string SourceDeviceName,
    [property: JsonPropertyOrder(1)] DateTimeOffset LastCopiedAt)
{
    /// <summary>
    /// Lowercase platform of the device that last copied the clip ("windows", "macos", "linux", "android", "web", "extension", ...),
    /// or null when unknown (e.g. a clip from a revoked device that predates platform tracking).
    /// Serialized right after <see cref="SourceDeviceName"/>.
    /// </summary>
    public string? SourceDevicePlatform { get; init; }

    /// <summary>When the clip was pinned, or null when it is not pinned. Pinned clips survive history clearing.</summary>
    public DateTimeOffset? PinnedAt { get; init; }

    /// <summary>
    /// The person who last copied the clip. Null while the server has only one person, so a household of one sees no
    /// names, and null when unknown.
    /// </summary>
    public string? SourceUserName { get; init; }

    /// <summary>The clip's content sealed under the vault key (<see cref="ClipPayload"/>, standard base64), or null on a clip from before encryption.</summary>
    public string? Payload { get; init; }

    /// <summary>The id of the vault key that sealed <see cref="Payload"/>.</summary>
    public string? KeyId { get; init; }

    /// <summary>True when the clip has a sealed thumbnail.</summary>
    public bool HasThumb { get; init; }

    /// <summary>How many sealed blobs the clip has.</summary>
    public int BlobCount { get; init; }

    /// <summary>
    /// The files of a file clip, in blob order. The server never sets it: clients fill it after opening the sealed payload,
    /// as they do <see cref="Text"/>, <see cref="Width"/> and <see cref="Height"/>.
    /// </summary>
    public IReadOnlyList<ClipFileInfo>? Files { get; init; }
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
    public const string VaultReset = "vault.reset";
}

public sealed record ServerLimitsDto(int TextKb, int ImageMb, int FileMb = 25);

public sealed record ServerInfoDto(int Protocol, IReadOnlyList<string> Kinds, ServerLimitsDto Limits);

public sealed record VaultKdfDto(string Alg, int MemoryKiB, int Iterations, int Parallelism, string Salt);

public sealed record VaultWrapDto(string KeyId, VaultKdfDto Kdf, string Nonce, string WrappedKey);

public sealed record HandoverCreateRequest(string Blob);

public sealed record HandoverCreated(Guid Id, DateTimeOffset ExpiresAt);

public sealed record HandoverBlob(string Blob);

public sealed record MeDto(string Name, bool IsOwner);

public sealed record ClipUploadMeta(string Kind, string KeyId, string Hash, string Payload, long SizeBytes,
    DateTimeOffset? CopiedAt, bool Backfill);
