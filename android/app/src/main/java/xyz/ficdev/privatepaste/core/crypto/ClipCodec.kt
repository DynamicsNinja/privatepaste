package xyz.ficdev.privatepaste.core.crypto

import kotlinx.serialization.Serializable
import xyz.ficdev.privatepaste.core.ContentHash
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ClipUploadMeta
import xyz.ficdev.privatepaste.core.model.ProtocolJson
import xyz.ficdev.privatepaste.core.model.ServerLimits
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.net.URI
import java.net.URISyntaxException
import java.security.GeneralSecurityException
import java.time.Instant
import java.util.Base64

/** The clip is over a limit; [message] is the copy to show. */
class ClipTooLargeException(message: String) : Exception(message)

/** What `POST /api/clips` takes: the meta, the sealed thumbnail if any, and the sealed blobs in order. */
class EncodedClip(val meta: ClipUploadMeta, val thumb: ByteArray?, val blobs: List<ByteArray>)

/**
 * The sealed payload: UTF-8 JSON with optional fields. Later releases add fields, so unknown ones are ignored, and
 * null ones are left out (ProtocolJson).
 */
@Serializable
private data class ClipPayload(val text: String? = null, val width: Int? = null, val height: Int? = null)

/**
 * Seals clips for upload and opens the ones the server sends, under one vault key. The server sees only the kind, the
 * keyed hash, the size and ciphertext. Never log what goes in or comes out.
 */
class ClipCodec(private val key: VaultKey) {

    val keyId: String get() = key.keyId

    fun encodeText(text: String, copiedAt: Instant?, backfill: Boolean): EncodedClip {
        val kind = if (isLink(text)) ClipKind.LINK else ClipKind.TEXT
        val meta = meta(kind, ContentHash.text(text), ClipPayload(text = text), text.toByteArray(Charsets.UTF_8).size.toLong(), copiedAt, backfill)
        return EncodedClip(meta, thumb = null, blobs = emptyList())
    }

    /**
     * Prepares the image ([ImagePreparation]) and seals it: blob 0 is the upright PNG, the thumbnail fits 256x256.
     * CPU heavy: call it off the main thread.
     * @throws ClipTooLargeException over 50 megapixels or over the server's `imageMb`.
     * @throws UnreadableImageException when the bytes can't be decoded.
     */
    fun encodeImage(bytes: ByteArray, copiedAt: Instant?, backfill: Boolean, limits: ServerLimits): EncodedClip {
        val image = ImagePreparation.prepare(bytes, limits.imageMb.toLong() * 1024 * 1024)
        val meta = meta(
            ClipKind.IMAGE, image.contentHash, ClipPayload(width = image.width, height = image.height),
            image.png.size.toLong(), copiedAt, backfill,
        )
        val blob = ByteArrayOutputStream().also { BlobCipher.encrypt(key, ByteArrayInputStream(image.png), it) }.toByteArray()
        return EncodedClip(meta, SealedBox.seal(key, "thumb", image.thumbPng), listOf(blob))
    }

    /** The server-visible hash of a content hash, to compare with the hashes of synced clips. */
    fun keyedHash(contentHash: String): String = KeyedHash.forHash(key, contentHash)

    /**
     * Fills in `text`, `width` and `height` from the sealed payload. Never throws for a bad clip: an unknown kind, a
     * clip sealed under another key, or a payload that doesn't open or parse comes back as [ClipKind.UNKNOWN], which
     * is shown but never copied.
     */
    fun decode(wire: ClipDto): ClipDto {
        if (wire.kind == ClipKind.UNKNOWN) return wire
        val unknown = wire.copy(kind = ClipKind.UNKNOWN, text = null, width = null, height = null)
        val sealed = wire.payload
        if (wire.keyId != key.keyId || sealed == null) return unknown
        val payload = try {
            val json = SealedBox.open(key, "payload", Base64.getDecoder().decode(sealed))
            ProtocolJson.json.decodeFromString(ClipPayload.serializer(), json.toString(Charsets.UTF_8))
        } catch (_: GeneralSecurityException) {
            return unknown
        } catch (_: IllegalArgumentException) {
            return unknown   // not base64; SerializationException is one too
        }
        return when (wire.kind) {
            ClipKind.TEXT, ClipKind.LINK ->
                payload.text?.let { wire.copy(text = it, width = null, height = null) } ?: unknown
            ClipKind.IMAGE -> wire.copy(text = null, width = payload.width, height = payload.height)
            ClipKind.UNKNOWN -> unknown
        }
    }

    /** @throws GeneralSecurityException if the thumbnail doesn't open under this key. */
    fun decodeThumb(bytes: ByteArray): ByteArray = SealedBox.open(key, "thumb", bytes)

    /** @throws GeneralSecurityException if the blob doesn't open under this key. */
    fun decodeBlob(bytes: ByteArray): ByteArray =
        ByteArrayOutputStream().also { BlobCipher.decrypt(key, ByteArrayInputStream(bytes), it) }.toByteArray()

    private fun meta(
        kind: ClipKind,
        contentHash: String,
        payload: ClipPayload,
        sizeBytes: Long,
        copiedAt: Instant?,
        backfill: Boolean,
    ): ClipUploadMeta {
        val json = ProtocolJson.json.encodeToString(ClipPayload.serializer(), payload).toByteArray(Charsets.UTF_8)
        return ClipUploadMeta(
            kind = kind.wire,
            keyId = key.keyId,
            hash = KeyedHash.forHash(key, contentHash),
            payload = Base64.getEncoder().encodeToString(SealedBox.seal(key, "payload", json)),
            sizeBytes = sizeBytes,
            copiedAt = copiedAt,
            backfill = backfill,
        )
    }

    internal companion object {
        /** Same rule as the desktop's ContentClassifier: one absolute http(s) URL and nothing else. */
        fun isLink(text: String): Boolean {
            val t = text.trim()
            if (t.isEmpty() || t.any { it.isWhitespace() }) return false
            val uri = try {
                URI(t)
            } catch (_: URISyntaxException) {
                return false
            }
            val scheme = uri.scheme?.lowercase() ?: return false
            return (scheme == "http" || scheme == "https") && !uri.host.isNullOrEmpty()
        }
    }
}
