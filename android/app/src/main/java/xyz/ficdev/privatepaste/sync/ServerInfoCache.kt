package xyz.ficdev.privatepaste.sync

import kotlinx.coroutines.CancellationException
import xyz.ficdev.privatepaste.core.model.ServerInfo

/**
 * What the server takes (its kinds and limits): asked on first use, and again after [invalidate], which the session
 * calls each time the sync link connects, so a server upgraded meanwhile takes effect without restarting the app.
 * Until a new ask succeeds the last answer stays in force; with no answer yet, the ask's failure propagates.
 */
class ServerInfoCache(private val fetch: suspend () -> ServerInfo) {
    @Volatile private var known: ServerInfo? = null
    @Volatile private var stale = false

    /** Ask again on next use. */
    fun invalidate() {
        stale = true
    }

    suspend fun get(): ServerInfo {
        val last = known
        if (last != null && !stale) return last
        return try {
            fetch().also {
                known = it
                stale = false
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            last ?: throw e
        }
    }
}
