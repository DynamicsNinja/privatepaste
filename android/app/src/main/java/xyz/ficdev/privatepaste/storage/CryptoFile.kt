package xyz.ficdev.privatepaste.storage

import android.util.Log
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.InvalidKeyException
import java.security.UnrecoverableKeyException
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import kotlinx.serialization.json.Json

/**
 * A file encrypted with AES-256-GCM under the key [alias] from [keys].
 * Layout: 12-byte IV || ciphertext || 16-byte tag. Thread-safe per instance; callers that share a path
 * must share the instance.
 */
class CryptoFile(val file: File, private val keys: KeyProvider, private val alias: String) {

    private val tmp = File(file.path + ".tmp")

    /**
     * The decrypted contents, or null when the file is missing or cannot be decrypted (truncated, tampered,
     * key gone or invalidated). Never throws; the reason is logged.
     */
    @Synchronized
    fun read(): ByteArray? {
        if (!file.isFile) return null
        return try {
            val data = file.readBytes()
            if (data.size < IV_SIZE + TAG_SIZE) {
                Log.w(TAG, "${file.name}: too short (${data.size} bytes)")
                return null
            }
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, keys.key(alias), GCMParameterSpec(TAG_SIZE * 8, data, 0, IV_SIZE))
            cipher.doFinal(data, IV_SIZE, data.size - IV_SIZE)
        } catch (e: Exception) {
            // AEADBadTagException, KeyPermanentlyInvalidatedException, UnrecoverableKeyException, ProviderException, IOException...
            Log.w(TAG, "${file.name}: unreadable (${e.javaClass.simpleName})")
            null
        }
    }

    /**
     * Encrypts [bytes] and replaces the file atomically (temp file + rename). Encryption is retried once:
     * - when the key itself is broken (an [InvalidKeyException], which includes KeyPermanentlyInvalidatedException,
     *   or an [UnrecoverableKeyException], anywhere in the cause chain), it is deleted and re-created first. Older
     *   files under the same alias were already unreadable.
     * - any other failure (a transient ProviderException or KeyStoreException) keeps the key, so the other files
     *   under the alias stay readable.
     * @throws IOException when the file cannot be written or encryption fails twice.
     */
    @Synchronized
    fun write(bytes: ByteArray) {
        val sealed = try {
            encrypt(bytes)
        } catch (e: Exception) {
            val broken = e.isBrokenKey()
            Log.w(TAG, "${file.name}: encryption failed (${e.javaClass.simpleName}); " +
                if (broken) "re-creating the key" else "retrying")
            try {
                if (broken) keys.deleteKey(alias)
                encrypt(bytes)
            } catch (retry: Exception) {
                throw IOException("Cannot encrypt ${file.name}", retry)
            }
        }
        replace(sealed)
    }

    private fun Throwable.isBrokenKey(): Boolean =
        generateSequence(this) { t -> t.cause?.takeIf { it !== t } }
            .take(MAX_CAUSE_DEPTH)
            .any { it is InvalidKeyException || it is UnrecoverableKeyException }

    /** Deletes the file (and any leftover temp file). */
    @Synchronized
    fun delete() {
        file.delete()
        tmp.delete()
    }

    private fun encrypt(bytes: ByteArray): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, keys.key(alias))   // the provider picks a random IV
        val iv = cipher.iv
        check(iv != null && iv.size == IV_SIZE) { "Unexpected IV size ${iv?.size}" }
        return iv + cipher.doFinal(bytes)
    }

    private fun replace(contents: ByteArray) {
        file.parentFile?.mkdirs()
        FileOutputStream(tmp).use { out ->
            out.write(contents)
            out.fd.sync()
        }
        var attempt = 1
        while (true) {
            try {
                try {
                    Files.move(tmp.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
                } catch (_: AtomicMoveNotSupportedException) {
                    Files.move(tmp.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING)
                }
                return
            } catch (e: IOException) {
                // Windows (unit tests): a scanner may hold the replaced file open for a moment.
                if (attempt >= MOVE_ATTEMPTS) throw e
                Thread.sleep(10L * attempt)
                attempt++
            }
        }
    }

    private companion object {
        const val TAG = "CryptoFile"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
        const val TAG_SIZE = 16
        const val MOVE_ATTEMPTS = 10
        const val MAX_CAUSE_DEPTH = 10
    }
}

/** JSON for the files under storage/: tolerant of unknown keys so older app versions can read newer files. */
internal object StorageJson {
    val json: Json = Json { ignoreUnknownKeys = true }
}
