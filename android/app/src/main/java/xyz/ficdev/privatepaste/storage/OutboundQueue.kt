package xyz.ficdev.privatepaste.storage

import android.util.Log
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.InstantSerializer
import java.io.File
import java.io.IOException
import java.time.Instant
import java.util.UUID

/** A clip waiting to be uploaded. Images keep their bytes in an encrypted file ([imageFile], a name in the image dir). */
@Serializable
data class QueuedClip(
    val id: String,
    val kind: ClipKind,
    val text: String?,
    val imageFile: String?,
    val mime: String?,
    @Serializable(InstantSerializer::class) val copiedAt: Instant,
    val attempts: Int = 0,
)

/**
 * Clips copied while offline, in FIFO order, persisted on every change ([file] under the [KeyAliases.QUEUE] key;
 * image bytes in [imageDir], encrypted with the same key). Loads on construction: a missing, corrupt or
 * undecryptable file starts empty, and image files no entry refers to are deleted. Thread-safe.
 *
 * [imageDir] must belong to this queue alone (and must not be the directory holding [file]): loading and [clear]
 * delete every file in it that no entry refers to.
 */
class OutboundQueue(private val file: CryptoFile, private val imageDir: File, private val keys: KeyProvider) {

    private val gate = Any()
    private val entries = ArrayList<QueuedClip>()

    /** Bumped by [clear]; an image written across a clear is dropped instead of queued. Guarded by [gate]. */
    private var generation = 0L

    /** Test hook: runs after an image file is written, before its entry is added. */
    internal var afterImageWrite: (() -> Unit)? = null

    val isEmpty: Boolean get() = synchronized(gate) { entries.isEmpty() }
    val size: Int get() = synchronized(gate) { entries.size }

    init {
        require(file.file.canonicalFile.parentFile != imageDir.canonicalFile) {
            "imageDir must not be the queue file's directory"
        }
        load()
    }

    fun enqueueText(text: String, copiedAt: Instant) {
        add(QueuedClip(UUID.randomUUID().toString(), ClipKind.TEXT, text, null, null, copiedAt))
    }

    /**
     * Stores [bytes] encrypted, then queues them. When [clear] runs meanwhile (sign-out), the image is deleted and
     * not queued. @throws IOException when the image cannot be written.
     */
    fun enqueueImage(bytes: ByteArray, mime: String, copiedAt: Instant) {
        val id = UUID.randomUUID().toString()
        val name = "$id$SUFFIX"
        val image = imageFile(name)
        val gen = synchronized(gate) { generation }
        try {
            image.write(bytes)   // unique name: written outside the lock, as it may be large
        } catch (e: IOException) {
            // clear() may have deleted the temp file under us: after a sign-out that is not an error.
            if (synchronized(gate) { generation } != gen) return image.delete()
            throw e
        }
        afterImageWrite?.invoke()
        synchronized(gate) {
            if (generation != gen) {
                image.delete()
                return
            }
            entries.add(QueuedClip(id, ClipKind.IMAGE, null, name, mime, copiedAt))
            persist()
        }
    }

    fun peek(): QueuedClip? = synchronized(gate) { entries.firstOrNull() }

    /** The image bytes of [c], or null when it is not an image or its file is gone or unreadable. */
    fun imageBytes(c: QueuedClip): ByteArray? = c.imageFile?.let { imageFile(it).read() }

    /** Drops the head item (uploaded, or given up on) and its image file. */
    fun removeHead() = synchronized(gate) {
        if (entries.isEmpty()) return@synchronized
        val head = entries.removeAt(0)
        head.imageFile?.let { imageFile(it).delete() }
        persist()
    }

    /** Counts one more failed upload of the head item (persisted); returns its attempt count, 0 when empty. */
    fun recordFailure(): Int = synchronized(gate) {
        if (entries.isEmpty()) return 0
        val head = entries[0].copy(attempts = entries[0].attempts + 1)
        entries[0] = head
        persist()
        head.attempts
    }

    /** Moves the head item to the back (keeping its attempts), so the items behind it get their turn. */
    fun moveHeadToBack() = synchronized(gate) {
        if (entries.size < 2) return@synchronized
        entries.add(entries.removeAt(0))
        persist()
    }

    /** Empties the queue and deletes its file and every image file (sign-out). */
    fun clear() = synchronized(gate) {
        generation++
        entries.clear()
        file.delete()
        imageDir.listFiles()?.forEach { it.delete() }
    }

    private fun add(clip: QueuedClip) = synchronized(gate) {
        entries.add(clip)
        persist()
    }

    private fun imageFile(name: String) = CryptoFile(File(imageDir, File(name).name), keys, KeyAliases.QUEUE)

    // Caller holds gate. A failed write is logged; the in-memory queue stays valid.
    private fun persist() {
        try {
            file.write(StorageJson.json.encodeToString(LIST, entries.toList()).encodeToByteArray())
        } catch (e: IOException) {
            Log.w(TAG, "Cannot save the outbound queue (${e.javaClass.simpleName})")
        }
    }

    private fun load() = synchronized(gate) {
        val bytes = file.read()
        if (bytes != null) {
            try {
                entries.addAll(
                    StorageJson.json.decodeFromString(LIST, bytes.decodeToString()).filter { e ->
                        if (e.kind == ClipKind.IMAGE) e.imageFile != null && File(imageDir, e.imageFile).isFile else e.text != null
                    },
                )
            } catch (e: Exception) {
                Log.w(TAG, "Outbound queue is corrupt; starting empty (${e.javaClass.simpleName})")
                entries.clear()
            }
        }
        val referenced = entries.mapNotNullTo(HashSet()) { it.imageFile }
        imageDir.listFiles()?.forEach { if (it.name !in referenced) it.delete() }
    }

    private companion object {
        const val TAG = "OutboundQueue"
        const val SUFFIX = ".bin"
        val LIST = ListSerializer(QueuedClip.serializer())
    }
}
