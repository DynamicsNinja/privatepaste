package xyz.ficdev.privatepaste.storage

import android.util.Log
import kotlinx.serialization.Serializable

@Serializable
data class StoredSession(val serverUrl: String, val deviceId: String, val deviceName: String, val token: String)

/** The signed-in session, encrypted at rest (use the [KeyAliases.SESSION] key). */
class SessionStore(private val file: CryptoFile) {

    /** The saved session, or null when signed out, or when the file is unreadable (treated as signed out). */
    fun load(): StoredSession? {
        val bytes = file.read() ?: return null
        return try {
            StorageJson.json.decodeFromString(StoredSession.serializer(), bytes.decodeToString())
        } catch (e: Exception) {
            Log.w(TAG, "Session file is not a session (${e.javaClass.simpleName})")
            null
        }
    }

    /** @throws java.io.IOException when the session cannot be written. */
    fun save(s: StoredSession) {
        file.write(StorageJson.json.encodeToString(StoredSession.serializer(), s).encodeToByteArray())
    }

    fun clear() = file.delete()

    private companion object {
        const val TAG = "SessionStore"
    }
}
