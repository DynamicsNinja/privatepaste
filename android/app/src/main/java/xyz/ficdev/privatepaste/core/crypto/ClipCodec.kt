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
     * @throws VaultKeyClosedException when the key was closed meanwhile: that says nothing about the clip.
     */
    fun decode(wire: ClipDto): ClipDto {
        // Never the server's own text, width or height: only what opens under this key is shown or searched.
        val unknown = wire.copy(kind = ClipKind.UNKNOWN, text = null, width = null, height = null)
        if (wire.kind == ClipKind.UNKNOWN) return unknown
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
        /**
         * Same rule as the desktop's ContentClassifier: one absolute http(s) URL and nothing else. It follows .NET's
         * System.Uri rather than java.net.URI, which refuses hosts with `_` and paths with `{ } | ^`; the cases both
         * must agree on are in docs/classifier-test-vectors.json. Anything they still disagree on only changes how a
         * clip looks: the server bumps a link onto a stored text and the other way round.
         */
        fun isLink(text: String): Boolean {
            val t = text.trim()
            if (t.isEmpty() || t.any { it.isWhitespace() }) return false
            val match = LINK_START.find(t) ?: return false
            val rest = t.substring(match.range.last + 1)
            val end = rest.indexOfAny(charArrayOf('/', '?', '#'))
            val authority = if (end < 0) rest else rest.substring(0, end)
            // Anything goes in the user info except a second @; the host and port must be valid.
            if (authority.substringBeforeLast('@', "").contains('@')) return false
            val hostPort = authority.substringAfterLast('@')
            val hostEnd = if (hostPort.startsWith('[')) hostPort.indexOf(']') + 1 else hostPort.indexOf(':')
            if (hostPort.startsWith('[') && hostEnd == 0) return false
            val split = if (hostEnd <= 0) hostPort.length else hostEnd
            return isHost(hostPort.substring(0, split)) && isPort(hostPort.substring(split))
        }

        private val LINK_START = Regex("^https?://", RegexOption.IGNORE_CASE)

        /** A bracketed IPv6 address, or dot-separated labels of letters, digits, `-`, `_` and non-ASCII. */
        private fun isHost(host: String): Boolean {
            if (host.startsWith('[')) {
                val address = host.substring(1, host.length - 1).substringBefore('%')
                return address.contains(':') && address.indexOf("::") == address.lastIndexOf("::") &&
                    address.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' || it == ':' || it == '.' }
            }
            val labels = host.split('.')
            // A trailing dot is allowed (an empty last label); any other empty label is not.
            return host.isNotEmpty() && labels.dropLast(1).none { it.isEmpty() } &&
                host.all { it.code > 0x7f || it.isLetterOrDigit() || it == '-' || it == '_' || it == '.' }
        }

        /** Empty, or `:` and digits up to 65535 (leading zeros allowed). */
        private fun isPort(port: String): Boolean {
            if (port.isEmpty()) return true
            val digits = port.substring(1)
            if (!port.startsWith(':') || !digits.all { it in '0'..'9' }) return false
            val value = digits.trimStart('0')
            return value.length <= 5 && (value.toIntOrNull() ?: 0) <= 65535
        }
    }
}
