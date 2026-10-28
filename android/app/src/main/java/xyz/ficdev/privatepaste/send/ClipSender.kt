package xyz.ficdev.privatepaste.send

import android.graphics.Bitmap
import android.graphics.ImageDecoder
import android.util.Log
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
) {
    private val flushLock = Mutex()

    suspend fun sendText(text: String): SendResult {
        if (text.toByteArray(Charsets.UTF_8).size > MAX_TEXT_BYTES) return SendResult.TooLarge(TEXT_TOO_LARGE)
        val copiedAt = clock.instant()
        if (ContentHash.text(text) == cache.lastAppliedHash) return SendResult.AlreadySynced
        return live(copiedAt, { queue.enqueueText(text, copiedAt) }) { api.uploadText(text, copiedAt, backfill = false) }
    }

    /** Reads at most 20 MB + 1 byte from the stream [open] returns (and closes it). */
    suspend fun sendImage(open: () -> InputStream, mime: String): SendResult {
        val copiedAt = clock.instant()
        val bytes = try {
            open().use { readLimited(it, MAX_IMAGE_BYTES) }
        } catch (e: IOException) {
            return SendResult.Rejected(UNREADABLE_IMAGE)
        } catch (e: SecurityException) {
            return SendResult.Rejected(UNREADABLE_IMAGE)
        } ?: return SendResult.TooLarge(IMAGE_TOO_LARGE)

        val hash = withContext(Dispatchers.Default) { uprightHash(bytes) }
        if (hash != null && hash == cache.lastAppliedHash) return SendResult.AlreadySynced
        return live(copiedAt, { queue.enqueueImage(bytes, mime, copiedAt) }) {
            api.uploadImage(bytes, fileNameFor(mime), mime, copiedAt, backfill = false)
        }
    }

    private suspend fun live(copiedAt: Instant, enqueue: () -> Unit, upload: suspend () -> ClipDto): SendResult =
        try {
            val clip = upload()
            cache.upsert(clip, fromSync = false)
            cache.save()
            capture.record(clip.lastCopiedAt)
            SendResult.Sent
        } catch (e: ApiException.Unauthorized) {
            SendResult.SignedOut
        } catch (e: ApiException.Rejected) {
            SendResult.Rejected(e.message ?: "The server refused the clip")
        } catch (e: ApiException) { // Transient, RateLimited
            try {
                enqueue()
                capture.record(copiedAt)
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
    suspend fun flushQueue(): Unit = flushLock.withLock {
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

    private companion object {
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
