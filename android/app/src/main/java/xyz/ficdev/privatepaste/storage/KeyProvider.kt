package xyz.ficdev.privatepaste.storage

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey

/** Supplies the AES-256 key used by [CryptoFile] for one alias, creating it on first use. */
interface KeyProvider {
    /** The key for [alias]; generated when it does not exist yet. May throw when the key store is broken. */
    fun key(alias: String): SecretKey

    /** Deletes the key for [alias], so the next [key] call generates a fresh one. */
    fun deleteKey(alias: String) {}
}

/** Key aliases. Each kind of data has its own key; thumbnails share the cache key, queued images the queue key. */
object KeyAliases {
    const val SESSION = "privatepaste.session"
    const val CACHE = "privatepaste.cache"
    const val QUEUE = "privatepaste.queue"
}

/**
 * Non-exportable AES-256-GCM keys in the Android Keystore. No user authentication is required, so the
 * foreground service can decrypt while the screen is locked. The Keystore picks a random IV for every encryption.
 */
class KeystoreKeyProvider : KeyProvider {
    private val lock = Any()

    override fun key(alias: String): SecretKey = synchronized(lock) {
        (keyStore().getKey(alias, null) as? SecretKey) ?: generate(alias)
    }

    override fun deleteKey(alias: String) = synchronized(lock) {
        keyStore().deleteEntry(alias)
    }

    private fun keyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    private fun generate(alias: String): SecretKey {
        val spec = KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .setRandomizedEncryptionRequired(true)
            .setUserAuthenticationRequired(false)
            .build()
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
            .apply { init(spec) }
            .generateKey()
    }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
    }
}
