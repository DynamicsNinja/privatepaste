using System.Text;
using PrivatePaste.Client.Core.Api;
using PrivatePaste.Client.Core.Files;
using PrivatePaste.Client.Core.Images;
using PrivatePaste.Client.Core.Platform;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;

namespace PrivatePaste.Client.Core.Vault;

/// <summary>The clip is over a limit; <see cref="Exception.Message"/> is the copy to show.</summary>
public sealed class ClipTooLargeException(string message) : Exception(message);

/// <summary>
/// The vault key was disposed (signed out, or replaced after a reset) while something still used it. Says nothing about
/// the clip: an upload stays queued, an apply is abandoned.
/// </summary>
public sealed class VaultKeyClosedException(Exception inner) : Exception("The vault key was closed.", inner);

/// <summary>
/// Seals clips for upload and opens the ones the server sends, under one vault key. The server sees only the kind, the
/// keyed hash, the size and ciphertext. Never log what goes in or comes out.
/// <para>
/// The codec borrows the key: its owner (the host) may dispose it at any time. <see cref="VaultKey"/> hands out copies
/// under a lock, so an operation that races the dispose either runs on the real key or throws
/// <see cref="VaultKeyClosedException"/>; nothing is ever sealed or opened with wiped bytes.
/// </para>
/// </summary>
public sealed class ClipCodec(VaultKey key)
{
    /// <summary>The id of the key this codec seals with (readable after the key is disposed).</summary>
    public string KeyId => key.KeyId;

    /// <summary>
    /// Seals a clipboard copy. Text is normalized to LF line ends, as every client stores it; an image is made upright and
    /// re-encoded as PNG with a thumbnail (<see cref="ImagePreparation"/>), off the calling thread.
    /// </summary>
    /// <exception cref="ClipTooLargeException">Over the server's text or image limit, or over 50 megapixels.</exception>
    /// <exception cref="InvalidDataException">An image that can't be decoded.</exception>
    /// <exception cref="VaultKeyClosedException">The key was disposed meanwhile.</exception>
    public async Task<ClipUpload> EncodeAsync(ClipboardContent content, DateTimeOffset? copiedAt, bool backfill,
        ServerLimitsDto limits, CancellationToken ct)
    {
        try
        {
            switch (content)
            {
                case ClipboardContent.Text t:
                {
                    var text = ContentHash.NormalizeText(t.Value);
                    var size = Encoding.UTF8.GetByteCount(text);
                    if (size > limits.TextKb * 1024L) throw new ClipTooLargeException(TextTooLargeMessage(limits.TextKb));
                    var kind = ContentClassifier.ClassifyText(text) == ClipKind.Link ? "link" : "text";
                    return new ClipUpload(Meta(kind, ContentHash.ForText(text), new ClipPayload { Text = text }, size, copiedAt, backfill),
                        null, []);
                }
                case ClipboardContent.Image i:
                {
                    var image = await Task.Run(() => ImagePreparation.Prepare(i.Png, limits.ImageMb * 1024L * 1024), ct)
                        .ConfigureAwait(false);
                    using var blob = new MemoryStream();
                    await BlobCipher.EncryptAsync(key, new MemoryStream(image.Png), blob, ct).ConfigureAwait(false);
                    var thumb = SealedBox.Seal(key, "thumb", image.ThumbPng);
                    var meta = Meta("image", image.ContentHash, new ClipPayload { Width = image.Width, Height = image.Height },
                        image.Png.Length, copiedAt, backfill);
                    return new ClipUpload(meta, thumb, [blob.ToArray()]);
                }
                default:
                    throw new ArgumentException("Unsupported content", nameof(content));
            }
        }
        catch (ObjectDisposedException e) { throw new VaultKeyClosedException(e); }
    }

    /// <summary>
    /// Seals copied files as one clip: each file is encrypted from disk into its own blob, in order; names, sizes and MIME
    /// types travel in the sealed payload. The caller checks the server's limits and hashed the files (<see cref="LocalFile"/>).
    /// File clips are never queued, so they are never a backfill.
    /// </summary>
    /// <exception cref="IOException">A file can't be read.</exception>
    /// <exception cref="VaultKeyClosedException">The key was disposed meanwhile.</exception>
    public async Task<ClipUpload> EncodeFilesAsync(IReadOnlyList<LocalFile> files, DateTimeOffset? copiedAt, CancellationToken ct)
    {
        try
        {
            var blobs = new List<byte[]>(files.Count);
            foreach (var f in files)
            {
                using var input = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                using var blob = new MemoryStream();
                await BlobCipher.EncryptAsync(key, input, blob, ct).ConfigureAwait(false);
                blobs.Add(blob.ToArray());
            }
            var payload = new ClipPayload { Files = files.Select(f => new ClipFileInfo(f.Name, f.Size, FileNames.MimeFor(f.Name))).ToList() };
            return new ClipUpload(Meta("file", LocalFile.HashOf(files), payload, files.Sum(f => f.Size), copiedAt, backfill: false),
                null, blobs);
        }
        catch (ObjectDisposedException e) { throw new VaultKeyClosedException(e); }
    }

    /// <summary>
    /// Fills in <see cref="ClipDto.Text"/>, <see cref="ClipDto.Width"/>, <see cref="ClipDto.Height"/> and <see cref="ClipDto.Files"/> from the sealed
    /// payload. Never throws for a bad clip: an unknown kind, a clip sealed under another key, or a payload that doesn't
    /// open or parse comes back as <see cref="ClipKind.Unknown"/>, which is shown but never copied.
    /// </summary>
    /// <exception cref="VaultKeyClosedException">The key was disposed meanwhile: that says nothing about the clip.</exception>
    public ClipDto Decode(ClipDto wire)
    {
        // Never the server's own text, width or height: only what opens under this key is shown or searched.
        var unknown = wire with { Kind = ClipKind.Unknown, Text = null, Width = null, Height = null, Files = null };
        if (wire.Kind == ClipKind.Unknown || wire.KeyId != key.KeyId || wire.Payload is null) return unknown;
        ClipPayload payload;
        try
        {
            payload = ClipPayload.Open(key, wire.Payload);
        }
        catch (ObjectDisposedException e) { throw new VaultKeyClosedException(e); }
        catch (Exception) { return unknown; }   // not base64, tampered with, not JSON: nothing to show
        return wire.Kind switch
        {
            ClipKind.Text or ClipKind.Link when payload.Text is { } text => wire with { Text = text, Width = null, Height = null },
            ClipKind.Image => wire with { Text = null, Width = payload.Width, Height = payload.Height },
            // One described file per blob, or it is not a clip this app can open.
            ClipKind.File when payload.Files is { Count: > 0 } files && files.Count == wire.BlobCount
                               && files.All(f => f is { Name: not null, Size: >= 0 }) =>
                wire with { Text = null, Width = null, Height = null, Files = files },
            _ => unknown,
        };
    }

    /// <summary>Opens a sealed thumbnail (a PNG).</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">It doesn't open under this key.</exception>
    /// <exception cref="VaultKeyClosedException">The key was disposed meanwhile.</exception>
    public byte[] DecodeThumb(byte[] sealedThumb)
    {
        try { return SealedBox.Open(key, "thumb", sealedThumb); }
        catch (ObjectDisposedException e) { throw new VaultKeyClosedException(e); }
    }

    /// <summary>Opens a sealed blob (an image clip's PNG).</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">It doesn't open under this key.</exception>
    /// <exception cref="VaultKeyClosedException">The key was disposed meanwhile.</exception>
    public async Task<byte[]> DecodeBlobAsync(byte[] blob, CancellationToken ct)
    {
        try
        {
            using var plaintext = new MemoryStream();
            await BlobCipher.DecryptAsync(key, new MemoryStream(blob), plaintext, ct).ConfigureAwait(false);
            return plaintext.ToArray();
        }
        catch (ObjectDisposedException e) { throw new VaultKeyClosedException(e); }
    }

    /// <summary>Opens a sealed blob into <paramref name="destination"/> (a file clip's file).</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// It doesn't open under this key, or was cut short or tampered with; chunks before the bad one may already be written.
    /// </exception>
    /// <exception cref="VaultKeyClosedException">The key was disposed meanwhile.</exception>
    public async Task DecodeBlobAsync(byte[] blob, Stream destination, CancellationToken ct)
    {
        try { await BlobCipher.DecryptAsync(key, new MemoryStream(blob), destination, ct).ConfigureAwait(false); }
        catch (ObjectDisposedException e) { throw new VaultKeyClosedException(e); }
    }

    /// <summary>"Clip too large to sync (text over 1 MB).", with the server's limit.</summary>
    public static string TextTooLargeMessage(int textKb) =>
        $"Clip too large to sync (text over {(textKb % 1024 == 0 ? $"{textKb / 1024} MB" : $"{textKb} KB")}).";

    private ClipUploadMeta Meta(string kind, string contentHash, ClipPayload payload, long sizeBytes,
        DateTimeOffset? copiedAt, bool backfill) =>
        new(kind, key.KeyId, KeyedHash.For(key, contentHash), ClipPayload.Seal(key, payload), sizeBytes, copiedAt, backfill);
}
