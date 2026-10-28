package xyz.ficdev.privatepaste.storage

import android.util.Log
import java.io.File
import java.io.IOException

/**
 * Encrypted thumbnail PNGs, one file per content hash (under the [KeyAliases.CACHE] key). Keyed by hash, so a
 * clip re-copied with the same content keeps its thumbnail. Best effort: failures are logged, never thrown.
 */
class ThumbnailStore(private val dir: File, private val keys: KeyProvider) {

    private val lock = Any()

    /** The thumbnail for [hash], or null when missing or unreadable. */
    fun get(hash: String): ByteArray? = synchronized(lock) { fileFor(hash)?.read() }

    fun put(hash: String, png: ByteArray) = synchronized(lock) {
        val file = fileFor(hash) ?: return@synchronized
        try {
            file.write(png)
        } catch (e: IOException) {
            Log.w(TAG, "Cannot store a thumbnail (${e.javaClass.simpleName})")
        }
    }

    /** Deletes every thumbnail whose hash is not in [keep]. */
    fun prune(keep: Set<String>) = synchronized(lock) {
        dir.listFiles()?.forEach { f ->
            if (!f.name.endsWith(SUFFIX) || f.name.removeSuffix(SUFFIX) !in keep) f.delete()   // also stray .tmp files
        }
    }

    fun clear() = synchronized(lock) {
        dir.listFiles()?.forEach { it.delete() }
    }

    private fun fileFor(hash: String): CryptoFile? {
        if (!SAFE_HASH.matches(hash)) {
            Log.w(TAG, "Ignoring a thumbnail with an unsafe hash")
            return null
        }
        return CryptoFile(File(dir, hash + SUFFIX), keys, KeyAliases.CACHE)
    }

    private companion object {
        const val TAG = "ThumbnailStore"
        const val SUFFIX = ".bin"
        val SAFE_HASH = Regex("[A-Za-z0-9_-]{1,128}")
    }
}
