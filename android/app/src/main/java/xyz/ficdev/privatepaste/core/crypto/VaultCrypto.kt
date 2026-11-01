package xyz.ficdev.privatepaste.core.crypto

import org.bouncycastle.crypto.generators.Argon2BytesGenerator
import org.bouncycastle.crypto.params.Argon2Parameters
import xyz.ficdev.privatepaste.core.model.VaultKdf
import xyz.ficdev.privatepaste.core.model.VaultWrap
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.security.GeneralSecurityException
import java.security.SecureRandom
import java.text.Normalizer
import java.util.Base64
import java.util.Locale
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

private fun utf8(text: String) = text.toByteArray(Charsets.UTF_8)

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
    try {
        return hmacSha256(prk, utf8(info) + byteArrayOf(1))
    } finally {
        prk.fill(0)
    }
}

private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it) }

/** The [VaultKey] was closed (signed out, or replaced after a reset) while something still wanted to use it. */
class VaultKeyClosedException : IllegalStateException("The vault key was closed.")

/**
 * The per-server vault key (VK) and the subkeys derived from it. The server never sees any of these. [close] wipes the
 * key material; any use afterwards throws [VaultKeyClosedException].
 *
 * Users never touch the live arrays: [useEncKey], [useHashKey] and [export] hand out a copy taken under the same lock
 * [close] takes, so a close that races a seal or an open either happens before the copy (the use throws) or after it
 * (the use finishes with the real key). No crypto ever runs on wiped bytes.
 */
class VaultKey private constructor(private val vk: ByteArray) : AutoCloseable {
    private val enc: ByteArray = hkdf(vk, "privatepaste v1 enc")
    private val hash: ByteArray = hkdf(vk, "privatepaste v1 hash")
    private val lock = Any()
    @Volatile private var closed = false

    /** Identifies the key without revealing it, so devices can tell whether they hold the server's current vault. */
    val keyId: String = hkdf(vk, "privatepaste v1 key id").let { full ->
        try {
            hex(full.copyOfRange(0, 8))
        } finally {
            full.fill(0)
        }
    }

    /** Runs [block] with a copy of the encryption subkey, wiped afterwards. */
    internal fun <T> useEncKey(block: (ByteArray) -> T): T = use(copyOf(enc), block)

    /** Runs [block] with a copy of the hash subkey, wiped afterwards. */
    internal fun <T> useHashKey(block: (ByteArray) -> T): T = use(copyOf(hash), block)

    /** A copy of the raw key; the caller owns it and should zero it when done. */
    fun export(): ByteArray = copyOf(vk)

    override fun close() {
        synchronized(lock) {
            closed = true
            vk.fill(0)
            enc.fill(0)
            hash.fill(0)
        }
    }

    /** True when every byte of key material is zero (test hook for [close]). */
    internal fun isWiped(): Boolean = synchronized(lock) { (vk + enc + hash).all { it == 0.toByte() } }

    private fun copyOf(bytes: ByteArray): ByteArray = synchronized(lock) {
        if (closed) throw VaultKeyClosedException()
        bytes.clone()
    }

    private fun <T> use(copy: ByteArray, block: (ByteArray) -> T): T {
        try {
            return block(copy)
        } finally {
            copy.fill(0)
        }
    }

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
        key.useEncKey { seal(it, aad(purpose), bytes, randomBytes(GCM_NONCE_BYTES)) }

    /** @throws GeneralSecurityException if the input is malformed, tampered with, or sealed under another key or purpose. */
    fun open(key: VaultKey, purpose: String, sealed: ByteArray): ByteArray = key.useEncKey { open(it, aad(purpose), sealed) }

    // Only the test vectors need a fixed nonce, hence the internal overloads.
    internal fun seal(key: VaultKey, purpose: String, bytes: ByteArray, nonce: ByteArray): ByteArray =
        key.useEncKey { seal(it, aad(purpose), bytes, nonce) }

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
        return utf8("privatepaste v1 $purpose")
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
    private val aadLabel = utf8("privatepaste v1 blob")

    fun encrypt(key: VaultKey, input: InputStream, output: OutputStream) = encrypt(key, input, output, randomBytes(PREFIX_SIZE))

    internal fun encrypt(key: VaultKey, input: InputStream, output: OutputStream, noncePrefix: ByteArray) =
        key.useEncKey { encKey -> encrypt(encKey, input, output, noncePrefix) }

    private fun encrypt(encKey: ByteArray, input: InputStream, output: OutputStream, noncePrefix: ByteArray) {
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
                val cipher = gcm(Cipher.ENCRYPT_MODE, encKey, nonce(noncePrefix, index), aad(index, isFinal))
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
    fun decrypt(key: VaultKey, input: InputStream, output: OutputStream) =
        key.useEncKey { encKey -> decrypt(encKey, input, output) }

    private fun decrypt(encKey: ByteArray, input: InputStream, output: OutputStream) {
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
            val plain = gcm(Cipher.DECRYPT_MODE, encKey, nonce(prefix, index), aad(index, isFinal)).doFinal(current, 0, currentLength)
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
    fun forHash(key: VaultKey, contentHash: String): String = key.useHashKey { hex(hmacSha256(it, utf8(contentHash))) }
}

class HandoverQr(val id: UUID, val otk: ByteArray, val keyId: String)

/**
 * Moves the vault key to a new device through a QR code: the server holds only the sealed blob, and the one-time key
 * that opens it travels in the QR text, so neither side alone can recover the vault key.
 */
object Handover {
    private const val QR_PREFIX = "privatepaste:handover?"
    private const val OTK_SIZE = 32
    private val aad = utf8("privatepaste v1 handover")
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
            if (key.keyId != expectedKeyId) {
                key.close()
                throw GeneralSecurityException("The handover holds a different vault key.")
            }
            return key
        } finally {
            vk.fill(0)
        }
    }

    fun qrText(id: UUID, otk: ByteArray, keyId: String): String =
        "${QR_PREFIX}id=$id&k=${Base64.getUrlEncoder().withoutPadding().encodeToString(otk)}&kid=$keyId"

    /**
     * Null when [text] isn't a handover code. A repeated field makes the code ambiguous, so it is rejected; unknown
     * fields are ignored. `k` must be the canonical unpadded base64url of the key.
     */
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
        // Canonical only: the decoder ignores the spare low bits of the last character, so "...Pj9" would open the
        // same key as "...Pj8". Every client accepts exactly the text qrText writes.
        if (Base64.getUrlEncoder().withoutPadding().encodeToString(otk) != kText) return null
        return HandoverQr(UUID.fromString(idText), otk, kidText)
    }
}

/** Thrown when the passphrase doesn't open a wrap. It can't tell a wrong passphrase from a damaged wrap. */
class WrongPassphraseException : GeneralSecurityException("That passphrase doesn't unlock this vault.")

/**
 * Wraps the vault key under a passphrase-derived key (Argon2id) so the server can store it without being able to open
 * it. [deriveKek], [wrap] and [unwrap] allocate about 64 MiB and take seconds: call them off the main thread
 * (Dispatchers.Default).
 */
object PassphraseWrap {
    const val MIN_LENGTH = 12
    const val RULE_MESSAGE = "Use at least 12 characters. A short sentence works well."
    const val COMMON_MESSAGE = "That passphrase is too common. Pick another."

    private const val MEMORY_KIB = 65536
    private const val ITERATIONS = 3
    private const val PARALLELISM = 1
    private const val SALT_BYTES = 16
    private const val WRAPPED_KEY_BYTES = VaultKey.LENGTH + GCM_TAG_BYTES

    // Client-side bounds on what a server-supplied wrap may ask for, so a hostile server can't exhaust memory or hang us.
    private val memoryRange = 65536..262144
    private val iterationsRange = 3..10
    private val parallelismRange = 1..8
    private val keyIdPattern = Regex("^[0-9a-f]{16}$")

    @Volatile
    private var common: Set<String> = emptySet()

    /**
     * Loads the bundled list (assets/common-passphrases.txt): one entry per line, `#` lines are comments. Entries are
     * NFC-normalized and lowercased once here, like the browser does. Until a list is loaded, only the length rule applies.
     */
    fun useCommonList(stream: InputStream) {
        common = stream.bufferedReader(Charsets.UTF_8).useLines { lines ->
            lines.filter { it.isNotEmpty() && !it.startsWith("#") }
                .map { normalize(it).lowercase(Locale.ROOT) }.toSet()
        }
    }

    /** Null when the passphrase is acceptable, otherwise the message to show. Same rule as the browser's passphraseProblem. */
    fun problem(passphrase: String): String? {
        val normalized = normalize(passphrase)
        if (normalized.length < MIN_LENGTH) return RULE_MESSAGE
        return if (normalized.lowercase(Locale.ROOT) in common) COMMON_MESSAGE else null
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

    /**
     * The wrap comes from the server, so it is validated before any work is done.
     * @throws WrongPassphraseException if the passphrase doesn't open the wrap (or the wrap's key id doesn't match its key).
     * @throws GeneralSecurityException if the wrap is malformed or asks for parameters outside the client bounds.
     */
    fun unwrap(wrap: VaultWrap, passphrase: String): VaultKey {
        val kdf = wrap.kdf
        val b64 = Base64.getDecoder()
        fun decode(text: String, size: Int, what: String): ByteArray {
            val bytes = try {
                b64.decode(text)
            } catch (e: IllegalArgumentException) {
                throw GeneralSecurityException("The vault wrap has a malformed $what.", e)
            }
            if (bytes.size != size) throw GeneralSecurityException("The vault wrap has a malformed $what.")
            return bytes
        }
        if (kdf.alg != "argon2id") throw GeneralSecurityException("The vault wrap uses an unsupported KDF.")
        if (!keyIdPattern.matches(wrap.keyId)) throw GeneralSecurityException("The vault wrap has a malformed key id.")
        if (kdf.memoryKiB !in memoryRange || kdf.iterations !in iterationsRange || kdf.parallelism !in parallelismRange) {
            throw GeneralSecurityException("The vault wrap asks for KDF parameters outside the supported range.")
        }
        val salt = decode(kdf.salt, SALT_BYTES, "salt")
        val nonce = decode(wrap.nonce, GCM_NONCE_BYTES, "nonce")
        val wrapped = decode(wrap.wrappedKey, WRAPPED_KEY_BYTES, "wrapped key")

        val kek = deriveKek(passphrase, salt, kdf.memoryKiB, kdf.iterations, kdf.parallelism)
        try {
            val vk = try {
                gcm(Cipher.DECRYPT_MODE, kek, nonce, aad(wrap.keyId)).doFinal(wrapped)
            } catch (_: GeneralSecurityException) {
                throw WrongPassphraseException()
            }
            try {
                if (vk.size != VaultKey.LENGTH) throw WrongPassphraseException()
                val key = VaultKey.fromBytes(vk)
                // The key id is bound into the AAD, but check it too so a swapped wrap can't pass for another vault.
                if (key.keyId != wrap.keyId) {
                    key.close()
                    throw WrongPassphraseException()
                }
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
        val password = utf8(normalize(passphrase))
        try {
            return ByteArray(32).also { Argon2BytesGenerator().apply { init(params) }.generateBytes(password, it) }
        } finally {
            password.fill(0)
        }
    }

    // NFC, so a keyboard that types "s + combining caron" unlocks the same vault as one that types it composed.
    private fun normalize(passphrase: String) = Normalizer.normalize(passphrase, Normalizer.Form.NFC)

    private fun aad(keyId: String) = utf8("privatepaste v1 vault $keyId")
}
