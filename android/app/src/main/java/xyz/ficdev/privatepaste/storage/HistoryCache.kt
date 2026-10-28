package xyz.ficdev.privatepaste.storage

import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.InstantSerializer
import java.io.IOException
import java.time.Instant

/**
 * Local, encrypted history of the most recent [max] clips (use the [KeyAliases.CACHE] key). Loads [file] on
 * construction: a missing, corrupt or undecryptable file starts empty. Thread-safe; call [save] after a batch of
 * changes. Thumbnails live in [ThumbnailStore], keyed by content hash, so a same-hash bump keeps its thumbnail.
 */
class HistoryCache(private val file: CryptoFile, private val max: Int = 200) {

    private val gate = Any()

    /** Serializes [save] and [clear]; never taken while holding [gate]. */
    private val saveLock = Any()

    private val list = ArrayList<ClipDto>()                 // newest first
    private val tombstones = HashMap<String, Long>()        // id -> delete seq
    private var seq = 0L
    private var appliedAt: Instant? = null
    private var appliedHash: String? = null

    private val _items = MutableStateFlow<List<ClipDto>>(emptyList())

    /** Snapshot, newest first (lastCopiedAt desc, then seq desc). */
    val items: StateFlow<List<ClipDto>> = _items.asStateFlow()

    /**
     * How far the sync stream (catch-up and live pushes) has been read. Only `upsert(fromSync = true)` and
     * [advanceSeq] move it: neither this device's own uploads nor [remove] do, so a clip uploaded while the socket is
     * down never makes the next catch-up skip other devices' clips with lower seqs. Persisted on its own, not derived
     * from the items.
     */
    val highestSeq: Long get() = synchronized(gate) { seq }

    /** The newest `lastCopiedAt` written to the clipboard from sync (persisted). */
    val lastAppliedAt: Instant? get() = synchronized(gate) { appliedAt }

    /** The hash of the clip that set [lastAppliedAt] (persisted). */
    val lastAppliedHash: String? get() = synchronized(gate) { appliedHash }

    init {
        load()
    }

    /**
     * Adds or replaces a clip. False when nothing changed: the clip is not newer (seq) than the one held for its id,
     * or its id has a tombstone with a seq ≥ the clip's. [fromSync] is false for the response to this device's own
     * upload (see [highestSeq]).
     */
    fun upsert(clip: ClipDto, fromSync: Boolean): Boolean = synchronized(gate) {
        if (fromSync && clip.seq > seq) seq = clip.seq
        // Broadcasts can arrive out of order: ignore anything older than what we hold or already deleted.
        val dead = tombstones[clip.id]
        if (dead != null && dead >= clip.seq) return false
        val index = list.indexOfFirst { it.id == clip.id }
        if (index >= 0 && clip.seq <= list[index].seq) return false
        tombstones.remove(clip.id)
        if (index >= 0) list.removeAt(index)
        list.add(clip)
        list.sortWith(NEWEST_FIRST)
        while (list.size > max) list.removeAt(list.lastIndex)
        publish()
        true
    }

    /** Removes a clip and records its tombstone (the higher seq wins). True when a held item was removed. */
    fun remove(id: String, seq: Long): Boolean = synchronized(gate) {
        val removed = list.removeAll { it.id == id }
        val prev = tombstones[id]
        if (prev == null || seq > prev) tombstones[id] = seq
        trimTombstones(tombstones)
        if (removed) publish()
        removed
    }

    /** Records that a clip copied [at] with [hash] went to the clipboard. Keeps the latest `at` and its hash. */
    fun markApplied(at: Instant, hash: String) = synchronized(gate) {
        val current = appliedAt
        if (current == null || at > current) {
            appliedAt = at
            appliedHash = hash
        }
    }

    /** Moves [highestSeq] forward (catch-up `latestSeq`); never backwards. */
    fun advanceSeq(seq: Long) = synchronized(gate) {
        if (seq > this.seq) this.seq = seq
    }

    /** Forgets everything and deletes the file (sign-out). A save in progress finishes first. */
    fun clear() {
        synchronized(saveLock) {
            synchronized(gate) {
                reset()
                publish()
            }
            file.delete()
        }
    }

    /**
     * Writes a snapshot to disk. Safe to call from several threads at once: saves are serialized, and each writes a
     * snapshot taken under the state lock. Write failures are logged, never thrown (the in-memory state stays valid).
     */
    fun save() {
        synchronized(saveLock) {
            val snapshot = synchronized(gate) {
                Snapshot(seq, appliedAt, appliedHash, list.toList(), HashMap(tombstones))
            }
            try {
                file.write(StorageJson.json.encodeToString(Snapshot.serializer(), snapshot).encodeToByteArray())
            } catch (e: IOException) {
                Log.w(TAG, "Cannot save the history cache (${e.javaClass.simpleName})")
            }
        }
    }

    private fun load() {
        val bytes = file.read() ?: return
        try {
            val snap = StorageJson.json.decodeFromString(Snapshot.serializer(), bytes.decodeToString())
            synchronized(gate) {
                list.addAll(snap.items.sortedWith(NEWEST_FIRST).take(max))
                tombstones.putAll(snap.tombstones)
                trimTombstones(tombstones)
                seq = snap.highestSeq
                appliedAt = snap.lastAppliedAt
                appliedHash = snap.lastAppliedHash
                publish()
            }
        } catch (e: Exception) {
            Log.w(TAG, "History cache is corrupt; starting empty (${e.javaClass.simpleName})")
            synchronized(gate) {
                reset()
                publish()
            }
        }
    }

    // Caller holds gate.
    private fun reset() {
        list.clear()
        tombstones.clear()
        seq = 0
        appliedAt = null
        appliedHash = null
    }

    // Caller holds gate, so emissions follow the order of the changes.
    private fun publish() {
        _items.value = list.toList()
    }

    @Serializable
    private data class Snapshot(
        val highestSeq: Long,
        @Serializable(InstantSerializer::class) val lastAppliedAt: Instant? = null,
        val lastAppliedHash: String? = null,
        val items: List<ClipDto>,
        val tombstones: Map<String, Long> = emptyMap(),
    )

    private companion object {
        const val TAG = "HistoryCache"
        const val MAX_TOMBSTONES = 1000

        val NEWEST_FIRST: Comparator<ClipDto> =
            compareByDescending<ClipDto> { it.lastCopiedAt }.thenByDescending { it.seq }

        /** Keeps the [MAX_TOMBSTONES] tombstones with the highest seqs. */
        fun trimTombstones(t: HashMap<String, Long>) {
            if (t.size <= MAX_TOMBSTONES) return
            t.entries.sortedBy { it.value }.take(t.size - MAX_TOMBSTONES).map { it.key }.forEach { t.remove(it) }
        }
    }
}
