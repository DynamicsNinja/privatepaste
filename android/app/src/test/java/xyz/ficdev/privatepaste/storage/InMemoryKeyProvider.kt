package xyz.ficdev.privatepaste.storage

import javax.crypto.KeyGenerator
import javax.crypto.SecretKey

/**
 * Test stand-in for [KeystoreKeyProvider]: Robolectric has no AndroidKeyStore. Generates an AES-256 key per alias
 * on first use, like the Keystore provider. [forget] simulates a key that is gone (app data restored, OEM wipe).
 */
class InMemoryKeyProvider : KeyProvider {
    private val keys = HashMap<String, SecretKey>()

    @Synchronized
    override fun key(alias: String): SecretKey =
        keys.getOrPut(alias) { KeyGenerator.getInstance("AES").apply { init(256) }.generateKey() }

    @Synchronized
    override fun deleteKey(alias: String) {
        keys.remove(alias)
    }

    /** Drops the key; the next [key] call generates a different one. */
    fun forget(alias: String) = deleteKey(alias)
}
