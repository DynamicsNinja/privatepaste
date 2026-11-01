package xyz.ficdev.privatepaste.send

import android.util.Log
import android.net.Uri
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.api.KeyResetException
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.api.UpdateRequiredException
import xyz.ficdev.privatepaste.core.crypto.ClipCodec
import xyz.ficdev.privatepaste.core.crypto.ClipTooLargeException
import xyz.ficdev.privatepaste.core.crypto.UnreadableImageException
import xyz.ficdev.privatepaste.core.crypto.VaultKeyClosedException
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ServerLimits
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.sync.LocalCaptureClock
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
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
        SendResult.SignedOut -> ClipSender.SIGN_IN_FIRST
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
 *
 * Every clip is sealed with [codec] at upload time; the queue keeps plaintext (encrypted at rest on this phone), so a
 * clip queued while locked or offline goes out under whatever key is current when it is sent. Without a codec a send
 * is queued and a flush waits. [limits] gives the server's limits (text is checked against `textKb`, images against
 * `imageMb`).
 * [onKeyReset] gets the id of the key the server refused as reset (409): queued clips are dropped then, since they
 * were meant for the history that the reset wiped. A key closed mid-send (sign-out, or a reset handled meanwhile)
 * never seals anything: the clip is queued instead, or dropped once the session is ending. [notifier] gets messages about queued clips only (live results
 * are returned to the caller to show).
 */
class ClipSender(
    private val api: PrivatePasteApi,
    private val codec: () -> ClipCodec?,
    private val limits: suspend () -> ServerLimits,
    private val queue: OutboundQueue,
    private val cache: HistoryCache,
    private val capture: LocalCaptureClock,
    private val notifier: (String) -> Unit,
    private val clock: Clock,
    private val onKeyReset: (keyId: String) -> Unit = {},
    private val isEnding: () -> Boolean = { false },
) {
    private val flushLock = Mutex()

    /**
     * Held while a live send records its outcome (cache, queue, capture clock). The host sets the flag behind
     * [isEnding] under this lock, so once it is set no send writes anything and none is half-way through.
     */
    val commitLock = Any()

    suspend fun sendText(text: String): SendResult = withContext(Dispatchers.IO) {
        // Offline, the server's limit is unknown: 1 MB (the default) is checked now, and the server has the last word.
        val textKb = try {
            limits().textKb
        } catch (e: ApiException) {
            DEFAULT_TEXT_KB
        }
        if (text.toByteArray(Charsets.UTF_8).size > textKb * 1024L) return@withContext SendResult.TooLarge(textTooLarge(textKb))
        val copiedAt = clock.instant()
        val enqueue = { queue.enqueueText(text, copiedAt) }
        val c = codec() ?: return@withContext queueOnly(copiedAt, enqueue)
        val encoded = try {
            c.encodeText(text, copiedAt = null, backfill = false)
        } catch (e: VaultKeyClosedException) {
            return@withContext queueOnly(copiedAt, enqueue)
        }
        // Synced clips carry the keyed hash, so that is what is compared.
        if (encoded.meta.hash == cache.lastAppliedHash) return@withContext SendResult.AlreadySynced
        live(c, copiedAt, enqueue) { api.uploadClip(encoded.meta, encoded.thumb, encoded.blobs) }
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
        val enqueue = { queue.enqueueImage(bytes, mime, copiedAt) }
        val c = codec() ?: return withContext(Dispatchers.IO) { queueOnly(copiedAt, enqueue) }
        val serverLimits = try {
            limits()
        } catch (e: ApiException.Unauthorized) {
            return SendResult.SignedOut
        } catch (e: ApiException) {
            return withContext(Dispatchers.IO) { queueOnly(copiedAt, enqueue) }   // offline: sealed when it is sent
        }
        // Decoding, rotating and re-encoding the image is CPU work.
        val encoded = try {
            withContext(Dispatchers.Default) { c.encodeImage(bytes, copiedAt = null, backfill = false, limits = serverLimits) }
        } catch (e: ClipTooLargeException) {
            return SendResult.TooLarge(e.message ?: IMAGE_TOO_LARGE)
        } catch (e: UnreadableImageException) {
            return SendResult.Rejected(UNREADABLE_IMAGE)
        } catch (e: VaultKeyClosedException) {
            return withContext(Dispatchers.IO) { queueOnly(copiedAt, enqueue) }
        }
        return withContext(Dispatchers.IO) {
            if (encoded.meta.hash == cache.lastAppliedHash) return@withContext SendResult.AlreadySynced
            live(c, copiedAt, enqueue) { api.uploadClip(encoded.meta, encoded.thumb, encoded.blobs) }
        }
    }

    /**
     * Uploads now; on a transient failure queues instead. Once the session is ending ([isEnding]) the outcome is not
     * recorded: no cache upsert or save, no enqueue, so a send still in flight never reaches the next session.
     */
    private suspend fun live(c: ClipCodec, copiedAt: Instant, enqueue: () -> Unit, upload: suspend () -> ClipDto): SendResult =
        try {
            val uploaded = upload()
            synchronized(commitLock) {
                if (isEnding()) return SendResult.SignedOut
                // Opened only now: once the session is ending, its vault key may already be closed. Closed by a reset
                // meanwhile, the clip still went out; it just isn't added to the history, which the reset wipes.
                try {
                    cache.upsert(c.decode(uploaded), fromSync = false)
                    cache.save()
                } catch (e: VaultKeyClosedException) {
                    Log.w(TAG, "The vault key was closed before the sent clip could be cached")
                }
                capture.record(uploaded.lastCopiedAt)
            }
            SendResult.Sent
        } catch (e: ApiException.Unauthorized) {
            SendResult.SignedOut
        } catch (e: KeyResetException) {
            onKeyReset(c.keyId)
            SendResult.Rejected(e.message ?: KEY_RESET)
        } catch (e: ApiException.Rejected) {
            SendResult.Rejected(e.message ?: "The server refused the clip")
        } catch (e: ApiException) { // Transient, RateLimited
            queueOnly(copiedAt, enqueue)
        }

    /** Keeps the clip for [flushQueue]; nothing is recorded once the session is ending. */
    private fun queueOnly(copiedAt: Instant, enqueue: () -> Unit): SendResult =
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

    /**
     * Seals and uploads queued clips in order as backfill with their original copy time, each at most once per call.
     * A transient failure counts an attempt: below 5 the flush stops here (the next one retries), from 5 the item moves
     * to the back, at 10 it is dropped with one notification. A rejection drops the item with the server's message,
     * except 410 (cleared), which drops it silently; an image over a limit, or one that doesn't decode, is dropped
     * with its message. A key reset (409) clears the whole queue and calls [onKeyReset] once. Without a codec nothing
     * is sent. [ApiException.Unauthorized], [UpdateRequiredException] and cancellation propagate (the item stays
     * queued, no attempt is counted).
     */
    suspend fun flushQueue(): Unit = flushLock.withLock { withContext(Dispatchers.IO) { flushLocked() } }

    private suspend fun flushLocked() {
        val c = codec() ?: return
        var remaining = queue.size
        while (remaining-- > 0) {
            val item = queue.peek() ?: return
            val clip: ClipDto = try {
                val encoded = when (item.kind) {
                    ClipKind.IMAGE -> {
                        val bytes = queue.imageBytes(item)
                        if (bytes == null) {
                            Log.w(TAG, "Queued image file is gone; dropping the entry")
                            queue.removeHead()
                            continue
                        }
                        val serverLimits = limits()
                        // Decoding, rotating and re-encoding the image is CPU work.
                        withContext(Dispatchers.Default) { c.encodeImage(bytes, item.copiedAt, backfill = true, limits = serverLimits) }
                    }
                    else -> c.encodeText(item.text.orEmpty(), item.copiedAt, backfill = true)
                }
                api.uploadClip(encoded.meta, encoded.thumb, encoded.blobs)
            } catch (e: CancellationException) {
                throw e
            } catch (e: ApiException.Unauthorized) {
                throw e
            } catch (e: UpdateRequiredException) {
                throw e
            } catch (e: VaultKeyClosedException) {
                return   // signed out or reset meanwhile: nothing is sealed under a closed key, the item stays
            } catch (e: KeyResetException) {
                // Everything queued was meant for the history the reset wiped: none of it is retried.
                queue.clear()
                onKeyReset(c.keyId)
                return
            } catch (e: ClipTooLargeException) {
                notifier(e.message ?: IMAGE_TOO_LARGE)
                queue.removeHead()
                continue
            } catch (e: UnreadableImageException) {
                notifier(UNREADABLE_IMAGE)
                queue.removeHead()
                continue
            } catch (e: ApiException.Rejected) {
                // 410: history was cleared (or the clip is too old) since it was copied. That is expected after a
                // clear, so the item goes quietly.
                if (e.status != GONE) notifier(e.message ?: "The server refused a queued clip")
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
            try {
                cache.upsert(c.decode(clip), fromSync = false)
                cache.save()
            } catch (e: VaultKeyClosedException) {
                return
            }
        }
    }

    internal companion object {
        const val TAG = "ClipSender"
        const val DEFAULT_TEXT_KB = 1024
        const val MAX_IMAGE_BYTES = 20 * 1024 * 1024
        const val ROTATE_AFTER_ATTEMPTS = 5
        const val DROP_AFTER_ATTEMPTS = 10
        const val GONE = 410
        const val IMAGE_TOO_LARGE = "Clip too large to sync (image over 20 MB)."
        const val GAVE_UP = "Couldn't sync a clip after several tries."
        const val UNREADABLE_IMAGE = "Couldn't read the image"
        const val KEY_RESET = "Encryption was reset."

        /** Same text as the desktop's: "Clip too large to sync (text over 1 MB)." */
        fun textTooLarge(textKb: Int): String =
            "Clip too large to sync (text over ${if (textKb % 1024 == 0) "${textKb / 1024} MB" else "$textKb KB"})."

        /** Same text as `R.string.sign_in_first`, for the send results (no Context here). */
        const val SIGN_IN_FIRST = "Sign in to PrivatePaste first"

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
