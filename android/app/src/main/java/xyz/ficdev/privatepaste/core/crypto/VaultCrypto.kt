package xyz.ficdev.privatepaste.core.crypto

import org.bouncycastle.crypto.generators.Argon2BytesGenerator
import org.bouncycastle.crypto.params.Argon2Parameters
import xyz.ficdev.privatepaste.core.model.VaultKdf
import xyz.ficdev.privatepaste.core.model.VaultWrap
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.security.GeneralSecurityException
import java.security.MessageDigest
import java.security.SecureRandom
import java.text.Normalizer
import java.util.Base64
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

// Kotlin port of src/PrivatePaste.Protocol/Crypto and the passphrase wrap of the desktop client. Every format here is
// pinned byte for byte by docs/e2e-test-vectors.json (VaultCryptoTest); never log keys, passphrases or QR text.

private const val GCM_TAG_BYTES = 16
private const val GCM_NONCE_BYTES = 12
private val random = SecureRandom()

private fun randomBytes(size: Int) = ByteArray(size).also { random.nextBytes(it) }

private fun ascii(text: String) = text.toByteArray(Charsets.UTF_8)

private fun gcm(mode: Int, key: ByteArray, nonce: ByteArray, aad: ByteArray): Cipher =
    Cipher.getInstance("AES/GCM/NoPadding").apply {
        init(mode, SecretKeySpec(key, "AES"), GCMParameterSpec(GCM_TAG_BYTES * 8, nonce))
        updateAAD(aad)
    }

private fun hmacSha256(key: ByteArray, data: ByteArray): ByteArray =
    Mac.getInstance("HmacSHA256").run {
        init(SecretKeySpec(key, "HmacSHA256"))
        doFinal(data)
    }

/** HKDF-SHA256 with no salt (RFC 5869 treats that as 32 zero bytes). Every length used here fits one block. */
private fun hkdf(ikm: ByteArray, info: String): ByteArray {
    val prk = hmacSha256(ByteArray(32), ikm)
    return hmacSha256(prk, ascii(info) + byteArrayOf(1))
}

private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it) }

/** The per-server vault key (VK) and the subkeys derived from it. The server never sees any of these. */
class VaultKey private constructor(private val vk: ByteArray) {
    internal val encKey: ByteArray = hkdf(vk, "privatepaste v1 enc")
    internal val hashKey: ByteArray = hkdf(vk, "privatepaste v1 hash")

    /** Identifies the key without revealing it, so devices can tell whether they hold the server's current vault. */
    val keyId: String = hex(hkdf(vk, "privatepaste v1 key id").copyOfRange(0, 8))

    /** A copy of the raw key; the caller owns it and should zero it when done. */
    fun export(): ByteArray = vk.clone()

    companion object {
        const val LENGTH = 32

        fun create() = VaultKey(randomBytes(LENGTH))

        fun fromBytes(bytes: ByteArray): VaultKey {
            require(bytes.size == LENGTH) { "A vault key is $LENGTH bytes." }
            return VaultKey(bytes.clone())
        }
    }
}

/** The small-item format: `0x01 || nonce(12) || ciphertext || tag(16)`, AAD `"privatepaste v1 " + purpose`. */
object SealedBox {
    private const val VERSION: Byte = 1
    private const val OVERHEAD = 1 + GCM_NONCE_BYTES + GCM_TAG_BYTES

    fun seal(key: VaultKey, purpose: String, bytes: ByteArray): ByteArray =
        seal(key.encKey, aad(purpose), bytes, randomBytes(GCM_NONCE_BYTES))

    /** @throws GeneralSecurityException if the input is malformed, tampered with, or sealed under another key or purpose. */
    fun open(key: VaultKey, purpose: String, sealed: ByteArray): ByteArray = open(key.encKey, aad(purpose), sealed)

    // Only the test vectors need a fixed nonce, hence the internal overloads.
    internal fun seal(key: VaultKey, purpose: String, bytes: ByteArray, nonce: ByteArray): ByteArray =
        seal(key.encKey, aad(purpose), bytes, nonce)

    internal fun seal(aesKey: ByteArray, aad: ByteArray, plaintext: ByteArray, nonce: ByteArray): ByteArray {
        require(nonce.size == GCM_NONCE_BYTES) { "The nonce is $GCM_NONCE_BYTES bytes." }
        return byteArrayOf(VERSION) + nonce + gcm(Cipher.ENCRYPT_MODE, aesKey, nonce, aad).doFinal(plaintext)
    }

    internal fun open(aesKey: ByteArray, aad: ByteArray, sealed: ByteArray): ByteArray {
        if (sealed.size < OVERHEAD || sealed[0] != VERSION) throw GeneralSecurityException("Not a sealed item.")
        val nonce = sealed.copyOfRange(1, 1 + GCM_NONCE_BYTES)
        return gcm(Cipher.DECRYPT_MODE, aesKey, nonce, aad).doFinal(sealed, 1 + GCM_NONCE_BYTES, sealed.size - 1 - GCM_NONCE_BYTES)
    }

    // "blob" has its own format and "handover" its own key, so only vault purposes go through the public API.
    private fun aad(purpose: String): ByteArray {
        require(purpose == "payload" || purpose == "thumb") { "Unknown purpose '$purpose'." }
        return ascii("privatepaste v1 $purpose")
    }
}

/**
 * Streaming format for large items: `0x02 || noncePrefix(8)`, then 1 MiB plaintext chunks, each `ciphertext || tag(16)`
 * with nonce `noncePrefix || uint32BE(index)`. Index and an is-final flag in the AAD make reordered, dropped or
 * appended chunks fail authentication.
 */
object BlobCipher {
    const val CHUNK_SIZE = 1048576

    private const val VERSION: Byte = 2
    private const val PREFIX_SIZE = 8
    private const val HEADER_SIZE = 1 + PREFIX_SIZE
    private val aadLabel = ascii("privatepaste v1 blob")

    fun encrypt(key: VaultKey, input: InputStream, output: OutputStream) = encrypt(key, input, output, randomBytes(PREFIX_SIZE))

    internal fun encrypt(key: VaultKey, input: InputStream, output: OutputStream, noncePrefix: ByteArray) {
        require(noncePrefix.size == PREFIX_SIZE) { "The nonce prefix is $PREFIX_SIZE bytes." }
        output.write(byteArrayOf(VERSION) + noncePrefix)
        // Reading one chunk ahead tells us whether the current one is final before it is sealed.
        var current = ByteArray(CHUNK_SIZE)
        var next = ByteArray(CHUNK_SIZE)
        try {
            var currentLength = input.readChunk(current)
            var index = 0
            while (true) {
                val nextLength = if (currentLength < CHUNK_SIZE) 0 else input.readChunk(next)
                val isFinal = nextLength == 0
                val cipher = gcm(Cipher.ENCRYPT_MODE, key.encKey, nonce(noncePrefix, index), aad(index, isFinal))
                output.write(cipher.doFinal(current, 0, currentLength))
                if (isFinal) return
                current = next.also { next = current }
                currentLength = nextLength
                index++
            }
        } finally {
            current.fill(0)
            next.fill(0)
        }
    }

    /**
     * Decrypts chunk by chunk, writing each one only after it authenticates. An exception can still come after earlier
     * chunks were written, so the caller must discard [output] on failure.
     * @throws GeneralSecurityException if the blob is malformed, tampered with, truncated, reordered or has trailing data.
     */
    fun decrypt(key: VaultKey, input: InputStream, output: OutputStream) {
        val header = ByteArray(HEADER_SIZE)
        if (input.readChunk(header) < HEADER_SIZE || header[0] != VERSION) throw GeneralSecurityException("Not an encrypted blob.")
        val prefix = header.copyOfRange(1, HEADER_SIZE)
        val chunkSize = CHUNK_SIZE + GCM_TAG_BYTES
        var current = ByteArray(chunkSize)
        var next = ByteArray(chunkSize)
        var currentLength = input.readChunk(current)
        var index = 0
        while (true) {
            if (currentLength < GCM_TAG_BYTES) throw GeneralSecurityException("The blob is truncated.")
            // A short read means end of stream, so that chunk is the last; a full one is last only if nothing follows.
            val nextLength = if (currentLength < chunkSize) 0 else input.readChunk(next)
            val isFinal = nextLength == 0
            val plain = gcm(Cipher.DECRYPT_MODE, key.encKey, nonce(prefix, index), aad(index, isFinal)).doFinal(current, 0, currentLength)
            try {
                output.write(plain)
            } finally {
                plain.fill(0)
            }
            if (isFinal) return
            current = next.also { next = current }
            currentLength = nextLength
            index++
        }
    }

    /** Reads until [buffer] is full or the stream ends; returns the byte count. */
    private fun InputStream.readChunk(buffer: ByteArray): Int {
        var total = 0
        while (total < buffer.size) {
            val n = read(buffer, total, buffer.size - total)
            if (n < 0) break
            total += n
        }
        return total
    }

    private fun nonce(prefix: ByteArray, index: Int) = prefix + ByteBuffer.allocate(4).putInt(index).array()

    private fun aad(index: Int, isFinal: Boolean) =
        aadLabel + ByteBuffer.allocate(4).putInt(index).array() + byteArrayOf(if (isFinal) 1 else 0)
}

/** The hash the server sees for dedup: a plain content hash would let anyone confirm a guessed clip, so it is keyed. */
object KeyedHash {
    /** Lowercase hex of HMAC-SHA256(hashKey, UTF8(contentHash)). */
    fun forHash(key: VaultKey, contentHash: String): String = hex(hmacSha256(key.hashKey, ascii(contentHash)))
}

class HandoverQr(val id: UUID, val otk: ByteArray, val keyId: String)

/**
 * Moves the vault key to a new device through a QR code: the server holds only the sealed blob, and the one-time key
 * that opens it travels in the QR text, so neither side alone can recover the vault key.
 */
object Handover {
    private const val QR_PREFIX = "privatepaste:handover?"
    private const val OTK_SIZE = 32
    private val aad = ascii("privatepaste v1 handover")
    private val keyIdPattern = Regex("^[0-9a-f]{16}$")
    private val uuidPattern = Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")

    /** Returns the one-time key and the blob to upload. */
    fun wrap(key: VaultKey): Pair<ByteArray, ByteArray> {
        val otk = randomBytes(OTK_SIZE)
        return otk to wrap(key, otk, randomBytes(GCM_NONCE_BYTES))
    }

    internal fun wrap(key: VaultKey, otk: ByteArray, nonce: ByteArray): ByteArray {
        val vk = key.export()
        try {
            return SealedBox.seal(otk, aad, vk, nonce)
        } finally {
            vk.fill(0)
        }
    }

    /** @throws GeneralSecurityException if the one-time key is wrong, the blob was tampered with, or the key inside isn't the one the QR promised. */
    fun unwrap(otk: ByteArray, blob: ByteArray, expectedKeyId: String): VaultKey {
        if (otk.size != OTK_SIZE) throw GeneralSecurityException("The handover key is the wrong length.")
        val vk = SealedBox.open(otk, aad, blob)
        try {
            if (vk.size != VaultKey.LENGTH) throw GeneralSecurityException("The handover holds no vault key.")
            val key = VaultKey.fromBytes(vk)
            if (key.keyId != expectedKeyId) throw GeneralSecurityException("The handover holds a different vault key.")
            return key
        } finally {
            vk.fill(0)
        }
    }

    fun qrText(id: UUID, otk: ByteArray, keyId: String): String =
        "${QR_PREFIX}id=$id&k=${Base64.getUrlEncoder().withoutPadding().encodeToString(otk)}&kid=$keyId"

    /** Null when [text] isn't a handover code. A repeated field makes the code ambiguous, so it is rejected; unknown fields are ignored. */
    fun parseQr(text: String): HandoverQr? {
        if (!text.startsWith(QR_PREFIX)) return null
        var idText: String? = null
        var kText: String? = null
        var kidText: String? = null
        for (pair in text.substring(QR_PREFIX.length).split('&')) {
            val eq = pair.indexOf('=')
            if (eq < 0) continue
            val value = pair.substring(eq + 1)
            when (pair.substring(0, eq)) {
                "id" -> if (idText == null) idText = value else return null
                "k" -> if (kText == null) kText = value else return null
                "kid" -> if (kidText == null) kidText = value else return null
            }
        }
        if (idText == null || kText == null || kidText == null) return null
        if (!uuidPattern.matches(idText) || !keyIdPattern.matches(kidText) || '=' in kText) return null
        val otk = try {
            Base64.getUrlDecoder().decode(kText)
        } catch (_: IllegalArgumentException) {
            return null
        }
        if (otk.size != OTK_SIZE) return null
        return HandoverQr(UUID.fromString(idText), otk, kidText)
    }
}

/** Thrown when the passphrase doesn't open a wrap. It can't tell a wrong passphrase from a damaged wrap. */
class WrongPassphraseException : GeneralSecurityException("That passphrase doesn't unlock this vault.")

/** Wraps the vault key under a passphrase-derived key (Argon2id) so the server can store it without being able to open it. */
object PassphraseWrap {
    const val MIN_LENGTH = 12
    const val RULE_MESSAGE = "Use at least 12 characters. A short sentence works well."
    const val COMMON_MESSAGE = "That passphrase is too common. Pick another."

    private const val MEMORY_KIB = 65536
    private const val ITERATIONS = 3
    private const val PARALLELISM = 1
    private const val SALT_BYTES = 16

    @Volatile
    private var common: Set<String> = emptySet()

    /**
     * Loads the bundled list (assets/common-passphrases.txt): one entry per line, `#` lines are comments. Comparison
     * ignores case. Until a list is loaded, only the length rule applies.
     */
    fun useCommonList(stream: InputStream) {
        common = stream.bufferedReader(Charsets.UTF_8).useLines { lines ->
            lines.map { it.trim() }.filter { it.isNotEmpty() && !it.startsWith("#") }.map { it.lowercase() }.toSet()
        }
    }

    /** Null when the passphrase is acceptable, otherwise the message to show. */
    fun problem(passphrase: String): String? {
        val normalized = normalize(passphrase)
        if (normalized.length < MIN_LENGTH) return RULE_MESSAGE
        return if (normalized.trim().lowercase() in common) COMMON_MESSAGE else null
    }

    fun wrap(key: VaultKey, passphrase: String): VaultWrap {
        val salt = randomBytes(SALT_BYTES)
        val nonce = randomBytes(GCM_NONCE_BYTES)
        val kek = deriveKek(passphrase, salt, MEMORY_KIB, ITERATIONS, PARALLELISM)
        val vk = key.export()
        try {
            val wrapped = gcm(Cipher.ENCRYPT_MODE, kek, nonce, aad(key.keyId)).doFinal(vk)
            val b64 = Base64.getEncoder()
            return VaultWrap(
                keyId = key.keyId,
                kdf = VaultKdf("argon2id", MEMORY_KIB, ITERATIONS, PARALLELISM, b64.encodeToString(salt)),
                nonce = b64.encodeToString(nonce),
                wrappedKey = b64.encodeToString(wrapped),
            )
        } finally {
            vk.fill(0)
            kek.fill(0)
        }
    }

    /** @throws WrongPassphraseException if the passphrase doesn't open the wrap (or the wrap's key id doesn't match its key). */
    fun unwrap(wrap: VaultWrap, passphrase: String): VaultKey {
        val kdf = wrap.kdf
        val b64 = Base64.getDecoder()
        val kek = try {
            deriveKek(passphrase, b64.decode(kdf.salt), kdf.memoryKiB, kdf.iterations, kdf.parallelism)
        } catch (e: IllegalArgumentException) {
            throw GeneralSecurityException("The vault wrap is malformed.", e)
        }
        try {
            val vk = try {
                gcm(Cipher.DECRYPT_MODE, kek, b64.decode(wrap.nonce), aad(wrap.keyId)).doFinal(b64.decode(wrap.wrappedKey))
            } catch (_: GeneralSecurityException) {
                throw WrongPassphraseException()
            }
            try {
                if (vk.size != VaultKey.LENGTH) throw WrongPassphraseException()
                val key = VaultKey.fromBytes(vk)
                // The key id is bound into the AAD, but check it too so a swapped wrap can't pass for another vault.
                if (!MessageDigest.isEqual(ascii(key.keyId), ascii(wrap.keyId))) throw WrongPassphraseException()
                return key
            } finally {
                vk.fill(0)
            }
        } finally {
            kek.fill(0)
        }
    }

    internal fun deriveKek(passphrase: String, salt: ByteArray, memoryKiB: Int, iterations: Int, parallelism: Int): ByteArray {
        val params = Argon2Parameters.Builder(Argon2Parameters.ARGON2_id)
            .withVersion(Argon2Parameters.ARGON2_VERSION_13)
            .withSalt(salt)
            .withMemoryAsKB(memoryKiB)
            .withIterations(iterations)
            .withParallelism(parallelism)
            .build()
        val password = ascii(normalize(passphrase))
        try {
            return ByteArray(32).also { Argon2BytesGenerator().apply { init(params) }.generateBytes(password, it) }
        } finally {
            password.fill(0)
        }
    }

    // NFC, so a keyboard that types "š" as s + combining caron unlocks the same vault as one that types it composed.
    private fun normalize(passphrase: String) = Normalizer.normalize(passphrase, Normalizer.Form.NFC)

    private fun aad(keyId: String) = ascii("privatepaste v1 vault $keyId")
}
