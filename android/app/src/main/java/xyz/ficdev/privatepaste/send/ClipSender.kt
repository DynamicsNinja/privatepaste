package xyz.ficdev.privatepaste.send

import android.graphics.Bitmap
import android.graphics.ImageDecoder
import android.util.Log
import android.net.Uri
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.core.BitmapHashing
import xyz.ficdev.privatepaste.core.ContentHash
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.sync.LocalCaptureClock
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.nio.ByteBuffer
import java.time.Clock
import java.time.Instant
import kotlin.coroutines.cancellation.CancellationException

sealed interface SendResult {
    data object Sent : SendResult
    data object Queued : SendResult
    data object AlreadySynced : SendResult
    data class TooLarge(val message: String) : SendResult
    data class Rejected(val message: String) : SendResult
    data object SignedOut : SendResult
}

/** The text a toast shows for this result (shared by the share target, the tile and the main screen). */
val SendResult.message: String
    get() = when (this) {
        SendResult.Sent -> "Sent to your devices"
        SendResult.Queued -> "Offline. Will send when connected."
        SendResult.AlreadySynced -> "Already on your other devices"
        is SendResult.TooLarge -> message
        is SendResult.Rejected -> message
        SendResult.SignedOut -> "Sign in to PrivatePaste first"
    }

/** Gives activities the app's [ClipSender]; null means signed out. Implemented by the Application. */
interface SenderProvider {
    val clipSender: ClipSender?

    /** App-lifetime scope: sends started by an activity run here so they outlive it. */
    val sendScope: CoroutineScope
}

/** Outcome of reading a shared image from its stream (done inside the activity, while the URI grant lasts). */
sealed interface ImageRead {
    class Bytes(val bytes: ByteArray) : ImageRead
    data object TooLarge : ImageRead
    data object Unreadable : ImageRead
}

private const val OWN_FILE_AUTHORITY = "xyz.ficdev.privatepaste.files"

/**
 * Only `content://` URIs of other apps may be read for a send. This app's own FileProvider (it serves the clip
 * images) and `file://` paths are refused, so a crafted share cannot make the app upload its own files.
 */
fun Uri.isSendable(): Boolean {
    if (!"content".equals(scheme, ignoreCase = true)) return false
    val authority = authority ?: return false
    return !authority.substringAfterLast('@').equals(OWN_FILE_AUTHORITY, ignoreCase = true)
}

internal fun ImageRead.toFailure(): SendResult? = when (this) {
    is ImageRead.Bytes -> null
    ImageRead.TooLarge -> SendResult.TooLarge(ClipSender.IMAGE_TOO_LARGE)
    ImageRead.Unreadable -> SendResult.Rejected(ClipSender.UNREADABLE_IMAGE)
}

/**
 * Phone to PC sending: a live upload when the user shares or taps the tile, a persistent queue when the server is
 * unreachable, and [flushQueue] (run by the sync engine on connect) to upload queued clips as backfill.
 * [notifier] gets messages about queued clips only (live results are returned to the caller to show).
 */
class ClipSender(
    private val api: PrivatePasteApi,
    private val queue: OutboundQueue,
    private val cache: HistoryCache,
    private val capture: LocalCaptureClock,
    private val notifier: (String) -> Unit,
    private val clock: Clock,
    private val isEnding: () -> Boolean = { false },
) {
    private val flushLock = Mutex()

    /**
     * Held while a live send records its outcome (cache, queue, capture clock). The host sets the flag behind
     * [isEnding] under this lock, so once it is set no send writes anything and none is half-way through.
     */
    val commitLock = Any()

    suspend fun sendText(text: String): SendResult = withContext(Dispatchers.IO) {
        if (text.toByteArray(Charsets.UTF_8).size > MAX_TEXT_BYTES) return@withContext SendResult.TooLarge(TEXT_TOO_LARGE)
        val copiedAt = clock.instant()
        if (ContentHash.text(text) == cache.lastAppliedHash) return@withContext SendResult.AlreadySynced
        live(copiedAt, { queue.enqueueText(text, copiedAt) }) { api.uploadText(text, copiedAt, backfill = false) }
    }

    /** Reads at most 20 MB + 1 byte from the stream [open] returns (and closes it), off the main thread. */
    suspend fun readImage(open: () -> InputStream): ImageRead = withContext(Dispatchers.IO) {
        try {
            val bytes = open().use { readLimited(it, MAX_IMAGE_BYTES) }
            if (bytes == null) ImageRead.TooLarge else ImageRead.Bytes(bytes)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            // IOException, SecurityException, or whatever a foreign ContentProvider throws: never strand the activity.
            Log.w(TAG, "Image read failed (${e.javaClass.simpleName})")
            ImageRead.Unreadable
        }
    }

    suspend fun sendImage(open: () -> InputStream, mime: String): SendResult {
        val read = readImage(open)
        return (read as? ImageRead.Bytes)?.let { sendImageBytes(it.bytes, mime) } ?: read.toFailure()!!
    }

    suspend fun sendImageBytes(bytes: ByteArray, mime: String): SendResult {
        val copiedAt = clock.instant()
        val hash = withContext(Dispatchers.Default) { uprightHash(bytes) }
        return withContext(Dispatchers.IO) {
            if (hash != null && hash == cache.lastAppliedHash) return@withContext SendResult.AlreadySynced
            live(copiedAt, { queue.enqueueImage(bytes, mime, copiedAt) }) {
                api.uploadImage(bytes, fileNameFor(mime), mime, copiedAt, backfill = false)
            }
        }
    }

    /**
     * Uploads now; on a transient failure queues instead. Once the session is ending ([isEnding]) the outcome is not
     * recorded: no cache upsert or save, no enqueue, so a send still in flight never reaches the next session.
     */
    private suspend fun live(copiedAt: Instant, enqueue: () -> Unit, upload: suspend () -> ClipDto): SendResult =
        try {
            val clip = upload()
            synchronized(commitLock) {
                if (isEnding()) return SendResult.SignedOut
                cache.upsert(clip, fromSync = false)
                cache.save()
                capture.record(clip.lastCopiedAt)
            }
            SendResult.Sent
        } catch (e: ApiException.Unauthorized) {
            SendResult.SignedOut
        } catch (e: ApiException.Rejected) {
            SendResult.Rejected(e.message ?: "The server refused the clip")
        } catch (e: ApiException) { // Transient, RateLimited
            try {
                synchronized(commitLock) {
                    if (isEnding()) return SendResult.SignedOut
                    enqueue()
                    capture.record(copiedAt)
                }
                SendResult.Queued
            } catch (io: IOException) {
                Log.w(TAG, "Cannot queue the clip (${io.javaClass.simpleName})")
                SendResult.Rejected("Couldn't save the clip to send later")
            }
        }

    /**
     * Uploads queued clips in order as backfill with their original copy time, each at most once per call. A transient
     * failure counts an attempt: below 5 the flush stops here (the next one retries), from 5 the item moves to the back,
     * at 10 it is dropped with one notification. A rejection drops the item with the server's message.
     * [ApiException.Unauthorized] and cancellation propagate (the item stays queued, no attempt is counted).
     */
    suspend fun flushQueue(): Unit = flushLock.withLock { withContext(Dispatchers.IO) { flushLocked() } }

    private suspend fun flushLocked() {
        var remaining = queue.size
        while (remaining-- > 0) {
            val item = queue.peek() ?: return
            val clip: ClipDto = try {
                when (item.kind) {
                    ClipKind.IMAGE -> {
                        val bytes = queue.imageBytes(item)
                        if (bytes == null) {
                            Log.w(TAG, "Queued image file is gone; dropping the entry")
                            queue.removeHead()
                            continue
                        }
                        api.uploadImage(bytes, fileNameFor(item.mime ?: "image/png"), item.mime ?: "image/png", item.copiedAt, backfill = true)
                    }
                    else -> api.uploadText(item.text.orEmpty(), item.copiedAt, backfill = true)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: ApiException.Unauthorized) {
                throw e
            } catch (e: ApiException.Rejected) {
                notifier(e.message ?: "The server refused a queued clip")
                queue.removeHead()
                continue
            } catch (e: ApiException) {
                val attempts = queue.recordFailure()
                when {
                    attempts >= DROP_AFTER_ATTEMPTS -> {
                        queue.removeHead()
                        notifier(GAVE_UP)
                    }
                    attempts < ROTATE_AFTER_ATTEMPTS -> return
                    else -> queue.moveHeadToBack()
                }
                continue
            }
            queue.removeHead() // uploaded: never send it twice
            cache.upsert(clip, fromSync = false)
            cache.save()
        }
    }

    /** Hash of the upright decoded pixels, or null when the image cannot be decoded or is too big to hash cheaply. */
    private fun uprightHash(bytes: ByteArray): String? {
        var bitmap: Bitmap? = null
        return try {
            bitmap = ImageDecoder.decodeBitmap(ImageDecoder.createSource(ByteBuffer.wrap(bytes))) { decoder, info, _ ->
                decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE // readable pixels; EXIF orientation is applied
                // A sampled bitmap would hash differently from the server's, so a huge image skips the check instead.
                if (info.size.width.toLong() * info.size.height > MAX_HASH_PIXELS) throw TooBigToHash()
            }
            BitmapHashing.hash(bitmap)
        } catch (e: CancellationException) {
            throw e
        } catch (e: TooBigToHash) {
            null
        } catch (e: Exception) {
            null // undecodable here: the server decides
        } catch (e: OutOfMemoryError) {
            null
        } finally {
            bitmap?.recycle()
        }
    }

    private class TooBigToHash : IOException()

    internal companion object {
        const val TAG = "ClipSender"
        const val MAX_TEXT_BYTES = 1024 * 1024
        const val MAX_IMAGE_BYTES = 20 * 1024 * 1024
        const val MAX_HASH_PIXELS = 24L * 1000 * 1000
        const val ROTATE_AFTER_ATTEMPTS = 5
        const val DROP_AFTER_ATTEMPTS = 10
        const val TEXT_TOO_LARGE = "Clip too large to sync (text over 1 MB)."
        const val IMAGE_TOO_LARGE = "Clip too large to sync (image over 20 MB)."
        const val GAVE_UP = "Couldn't sync a clip after several tries."
        const val UNREADABLE_IMAGE = "Couldn't read the image"

        fun fileNameFor(mime: String): String = when (mime.lowercase()) {
            "image/jpeg", "image/jpg" -> "clip.jpg"
            "image/gif" -> "clip.gif"
            "image/webp" -> "clip.webp"
            "image/bmp" -> "clip.bmp"
            else -> "clip.png"
        }

        /** Reads up to [limit] bytes; null when the stream has more (after reading at most limit + 1 bytes). */
        fun readLimited(input: InputStream, limit: Int): ByteArray? {
            val out = ByteArrayOutputStream()
            val buffer = ByteArray(64 * 1024)
            var total = 0
            while (total <= limit) {
                val n = input.read(buffer, 0, minOf(buffer.size, limit + 1 - total))
                if (n < 0) break
                out.write(buffer, 0, n)
                total += n
            }
            return if (total > limit) null else out.toByteArray()
        }
    }
}
