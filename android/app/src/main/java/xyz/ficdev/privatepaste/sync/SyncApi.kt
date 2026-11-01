package xyz.ficdev.privatepaste.sync

import xyz.ficdev.privatepaste.core.model.SyncPage

/** The two server calls the sync engine makes. Implemented by `PrivatePasteApi`; tests supply a fake. */
interface SyncApi {
    suspend fun sync(afterSeq: Long, limit: Int = 200): SyncPage
    /** The sealed thumbnail (ciphertext). */
    suspend fun thumb(id: String): ByteArray
}
