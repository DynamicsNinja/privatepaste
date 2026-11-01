using PrivatePaste.Protocol;

namespace PrivatePaste.Client.Core.Api;

/// <summary>What <c>POST /api/clips</c> takes: the metadata, the sealed thumbnail if any, and the sealed blobs in order.</summary>
public sealed record ClipUpload(ClipUploadMeta Meta, byte[]? Thumb, IReadOnlyList<byte[]> Blobs);
