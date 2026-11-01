package xyz.ficdev.privatepaste.storage

import android.util.Log
import xyz.ficdev.privatepaste.core.crypto.VaultKey

/**
 * The vault key kept on this phone, encrypted at rest like the session (use the [KeyAliases.VAULT] key). Each [load]
 * returns a new [VaultKey] the caller owns and closes.
 */
class VaultKeyStore(private val file: CryptoFile) {

    /** The stored key, or null when there is none or the file is unreadable (the vault then shows as locked). */
    fun load(): VaultKey? {
        val bytes = file.read() ?: return null
        try {
            if (bytes.size != VaultKey.LENGTH) {
                Log.w(TAG, "Vault key file has the wrong length")
                return null
            }
            return VaultKey.fromBytes(bytes)
        } finally {
            bytes.fill(0)
        }
    }

    /** @throws java.io.IOException when the key cannot be written. */
    fun save(key: VaultKey) {
        val raw = key.export()
        try {
            file.write(raw)
        } finally {
            raw.fill(0)
        }
    }

    fun delete() = file.delete()

    private companion object {
        const val TAG = "VaultKeyStore"
    }
}
