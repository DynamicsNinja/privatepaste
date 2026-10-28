package xyz.ficdev.privatepaste.sync

import android.util.Log
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.WsEvent
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import java.io.IOException
import java.time.Clock
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Keeps the local history in live sync with the server and hands clips from other devices to [applier].
 *
 * Per connection: connect ([links]), catch up via `/api/sync` (applying at most the newest caught-up clip from
 * another device), run [flushQueue], then receive pushes, with an app-level `{"type":"ping"}` every 25 s and a 10 s
 * pong timeout. While connected, a non-empty [queue] is flushed again every 30 s. A 1008 close or a 401 from any call
 * (including [flushQueue] and thumbnail fetches) emits [revoked] once and stops for good ([state] = SIGNED_OUT); any
 * other failure sets OFFLINE and reconnects after [Backoff.next]. The backoff is reset only after a link stayed up
 * 25 s, or by [nudge].
 *
 * Catch-up applies a clip only when something was applied before (`lastAppliedAt` set, so the first sync after
 * sign-in is history only), the clip is the top of the history, it is newer than `lastAppliedAt` and than
 * [LocalCaptureClock.lastLocalCaptureAt], and the offline queue is empty.
 *
 * Cache saves (and thumbnail pruning) are throttled to one every 2 s, plus one when a link ends and on [stop].
 * All timing uses [clock] and the dispatcher of [scope]; in tests both follow virtual time. Blocking file IO runs on
 * [scope]'s dispatcher, so give it an IO-capable one.
 *
 * The host must stop the engine before clearing the session (sign-out), so no save runs after the clear.
 */
class SyncEngine(
    private val api: SyncApi,
    private val cache: HistoryCache,
    private val queue: OutboundQueue,
    private val thumbs: ThumbnailStore,
    private val applier: ClipApplier,
    private val flushQueue: suspend () -> Unit,
    private val links: LinkFactory,
    private val ownDeviceId: String,
    private val capture: LocalCaptureClock,
    private val clock: Clock,
    private val backoff: Backoff,
    private val scope: CoroutineScope,
) {
    private val _state = MutableStateFlow(SyncState.OFFLINE)
    val state: StateFlow<SyncState> = _state.asStateFlow()

    private val revokedSignal = CompletableDeferred<Unit>()

    /** Emits once (also to a late collector) after a 1008 close or a 401; the engine has stopped by then. */
    val revoked: Flow<Unit> = flow {
        revokedSignal.await()
        emit(Unit)
    }

    private enum class Outcome { FAILED, REVOKED }

    private val lock = Any()
    private var job: Job? = null                      // the current run; guarded by lock
    private var stopping: CompletableDeferred<Unit>? = null   // the latest stop still unwinding; guarded by lock
    private val signedOut = AtomicBoolean(false)

    // nudge / background revoke: what to interrupt right now.
    private val nudged = AtomicBoolean(false)
    private val revokeRequested = AtomicBoolean(false)
    private val wake = Channel<Unit>(Channel.CONFLATED)
    @Volatile private var attempt: Job? = null

    /** The run scope: thumbnail fetches outlive a single link. */
    @Volatile private var workers: CoroutineScope? = null
    private val thumbSlots = Semaphore(MAX_THUMBNAIL_FETCHES)
    private val thumbsInFlight: MutableSet<String> = ConcurrentHashMap.newKeySet()

    private val saveLock = Any()
    private var dirty = false                         // guarded by saveLock
    private var lastSaveAt: Instant? = null           // guarded by saveLock

    /** The last clip handed to the applier (id, seq): a repeated broadcast is not applied twice. */
    @Volatile private var lastApplied: Pair<String, Long>? = null

    /**
     * Starts the loop in [scope]. No-op while running or after a revoke. Called while a [stop] is still unwinding,
     * the new run waits for that stop (and its save) to finish first, so two runs never overlap.
     */
    fun start() {
        synchronized(lock) {
            if (job != null || signedOut.get()) return
            val previous = stopping
            val j = scope.launch(start = CoroutineStart.LAZY) {
                previous?.await()
                if (signedOut.get()) return@launch   // the stop we waited for ended in a revoke
                run()
            }
            job = j
            j.start()
        }
    }

    /**
     * Stops the loop, closes the link and saves. Safe to call when not running. A concurrent second call returns
     * only once the first has finished.
     */
    suspend fun stop() {
        val done = CompletableDeferred<Unit>()
        var toStop: Job? = null
        var earlier: CompletableDeferred<Unit>? = null
        synchronized(lock) {
            val current = job
            earlier = stopping
            if (current != null) {
                toStop = current
                job = null
                stopping = done
            }
        }
        val j = toStop
        val previous = earlier
        if (j == null) {
            previous?.await()   // another stop is unwinding: return once it has finished
            return
        }
        // In the engine's scope, not the caller's: a caller cancelled mid-stop never leaves `stopping` pending.
        scope.launch(start = CoroutineStart.UNDISPATCHED) {
            try {
                previous?.await()
                j.cancelAndJoin()
            } finally {
                withContext(NonCancellable) {
                    saveIfDirty()
                    synchronized(lock) {
                        if (stopping === done) stopping = null
                        if (job == null && !signedOut.get()) _state.value = SyncState.OFFLINE
                    }
                    done.complete(Unit)
                }
            }
        }
        done.await()
    }

    /**
     * The network or power state changed: drops the current connection attempt or link, skips any backoff wait,
     * resets the backoff and reconnects at once. Thread-safe; no effect while stopped.
     */
    fun nudge() {
        nudged.set(true)          // before reading attempt: the loop checks the flag after publishing attempt
        attempt?.cancel()
        wake.trySend(Unit)
    }

    private suspend fun run() {
        val self = currentCoroutineContext().job
        var mine: CoroutineScope? = null
        try {
            val revoked = coroutineScope {
                mine = this
                workers = this
                pruneThumbnails()
                try {
                    loop()
                } finally {
                    coroutineContext.cancelChildren()   // thumbnail fetches; coroutineScope waits for them
                }
            }
            if (revoked) {
                saveIfDirty()     // before the host hears of it and clears the cache
                signOut()
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            report("Sync stopped unexpectedly", e)
            if (!signedOut.get()) _state.value = SyncState.OFFLINE
        } finally {
            synchronized(lock) {
                if (workers === mine) workers = null
                if (job === self && !signedOut.get()) job = null
            }
        }
    }

    /** Returns true when revoked; otherwise runs until cancelled. */
    private suspend fun loop(): Boolean {
        while (true) {
            currentCoroutineContext().ensureActive()
            wake.tryReceive()     // this attempt is the fresh start a pending nudge asks for
            nudged.set(false)
            _state.value = SyncState.CONNECTING
            if (connectOnce() == Outcome.REVOKED || revokeRequested.get()) return true
            if (nudged.getAndSet(false)) {
                backoff.reset()   // the network just changed: retry at once, with fresh delays
                continue
            }
            val wait = backoff.next()
            _state.value = SyncState.OFFLINE
            val woke = withTimeoutOrNull(wait) { wake.receive() } != null
            if (revokeRequested.get()) return true
            if (woke) backoff.reset()
        }
    }

    private suspend fun connectOnce(): Outcome = supervisorScope {
        val a = async { connection() }
        attempt = a
        if (nudged.get() || revokeRequested.get()) a.cancel()   // interrupted before attempt was visible
        try {
            a.await()
        } catch (e: CancellationException) {
            ensureActive()        // stop(): propagate; a nudge only cancelled this attempt
            Outcome.FAILED
        } catch (e: Exception) {
            report("Sync connection failed", e)
            Outcome.FAILED
        } finally {
            attempt = null
        }
    }

    private suspend fun connection(): Outcome {
        val link = try {
            links.connect()
        } catch (e: CancellationException) {
            throw e
        } catch (e: ApiException.Unauthorized) {
            return Outcome.REVOKED
        } catch (e: Exception) {
            report("Could not connect to the server", e)
            return Outcome.FAILED
        }

        var connectedAt: Instant? = null
        try {
            coroutineScope {
                catchUp()
                runFlush()
                val pings = Pings(clock.instant())
                connectedAt = clock.instant()
                _state.value = SyncState.CONNECTED
                val monitor = launch { monitor(link, pings) }
                link.events.collect { handle(it, pings) }
                monitor.cancel()  // the server closed the socket (1001 or another non-1008 code)
            }
            return Outcome.FAILED
        } catch (e: CancellationException) {
            throw e
        } catch (e: ApiException.Unauthorized) {
            return Outcome.REVOKED
        } catch (e: RevokedException) {
            return Outcome.REVOKED
        } catch (e: Exception) {
            report("Connection to the server lost", e)
            return Outcome.FAILED
        } finally {
            link.close(NORMAL_CLOSURE, "")
            saveIfDirty()
            // Only a link that proved stable resets the delays: a server that accepts and drops at once still backs off.
            val since = connectedAt
            if (since != null && elapsedMs(since, clock.instant()) >= PING_INTERVAL_MS) backoff.reset()
        }
    }

    private suspend fun catchUp() {
        val caught = ArrayList<ClipDto>()
        while (true) {
            val before = cache.highestSeq
            val page = api.sync(before)
            for (clip in page.clips) {
                cache.upsert(clip, fromSync = true)
                caught += clip
                requestThumbnail(clip)
            }
            // The server's latestSeq is its global counter, also on a truncated page, whose tombstones only reach its
            // last clip: continue from there, and from latestSeq once the last page is read.
            val upTo = if (page.hasMore && page.clips.isNotEmpty()) page.clips.last().seq else page.latestSeq
            for (id in page.deletedIds) cache.remove(id, upTo)
            cache.advanceSeq(upTo)
            markDirty()
            if (!page.hasMore || cache.highestSeq <= before) break
        }
        saveIfDue()

        val lastAppliedAt = cache.lastAppliedAt ?: return   // first sync after sign-in: history only
        if (!queue.isEmpty) return                          // a local copy still queued is newer than anything here
        val items = cache.items.value
        val present = items.associate { it.id to it.seq }
        val newest = caught
            .filter { it.sourceDeviceId != ownDeviceId && present[it.id] == it.seq }
            .maxWithOrNull(compareBy<ClipDto> { it.lastCopiedAt }.thenBy { it.seq })
            ?: return
        val top = items.firstOrNull() ?: return
        if (top.id != newest.id || top.seq != newest.seq) return
        if (!newest.lastCopiedAt.isAfter(lastAppliedAt)) return
        val localCopy = capture.lastLocalCaptureAt
        if (localCopy != null && !newest.lastCopiedAt.isAfter(localCopy)) return
        apply(newest)
    }

    private suspend fun handle(event: WsEvent, pings: Pings) {
        when (event) {
            is WsEvent.Pong -> pings.pong()
            is WsEvent.ClipCreated -> {
                val clip = event.clip
                val changed = cache.upsert(clip, fromSync = true)   // recorded before the apply, which a stop can cancel
                markDirty()
                saveIfDue()
                requestThumbnail(clip)
                // Never a stale clip: one whose delete (tombstone) or newer version came first, or one older than the
                // top of the history (a late broadcast, or a newer copy made here).
                val top = cache.items.value.firstOrNull()
                val appliedAt = cache.lastAppliedAt
                if (clip.sourceDeviceId != ownDeviceId && event.live && changed &&
                    top != null && top.id == clip.id && top.seq == clip.seq &&
                    lastApplied != (clip.id to clip.seq) &&
                    (appliedAt == null || !appliedAt.isAfter(clip.lastCopiedAt))
                ) {
                    // A slow apply (image download) holds up this loop and with it the pong: not a dead link.
                    pings.setBusy(true)
                    try {
                        apply(clip)
                    } finally {
                        pings.setBusy(false)
                    }
                }
            }
            is WsEvent.ClipDeleted -> {
                cache.remove(event.id, event.seq)
                cache.advanceSeq(event.seq)
                markDirty()
                saveIfDue()
            }
            WsEvent.Unknown -> Unit
        }
    }

    /** 401 and cancellation propagate; any other failure is logged and the clip skipped. */
    private suspend fun apply(clip: ClipDto) {
        try {
            if (applier.apply(clip)) {
                cache.markApplied(clip.lastCopiedAt, clip.hash)
                markDirty()
            }
            lastApplied = clip.id to clip.seq
        } catch (e: CancellationException) {
            throw e
        } catch (e: ApiException.Unauthorized) {
            throw e
        } catch (e: Exception) {
            report("Could not put a clip from another device on the clipboard", e)
        }
    }

    /** Every second while connected: ping/pong bookkeeping, the periodic queue flush, the throttled save. */
    private suspend fun monitor(link: WsLink, pings: Pings) = coroutineScope {
        var lastFlushAt = clock.instant()   // the connect itself just flushed
        var flush: Job? = null
        while (true) {
            delay(TICK_MS)
            val now = clock.instant()
            when (pings.check(now)) {
                Pings.Due.TIMEOUT -> throw IOException("No pong from the server")
                Pings.Due.SEND -> if (!link.send(PING)) throw IOException("Cannot send a ping")
                Pings.Due.NONE -> Unit
            }
            if (elapsedMs(lastFlushAt, now) >= FLUSH_INTERVAL_MS) {
                lastFlushAt = now
                // In the background: a slow upload must not hold up the pings. A 401 fails this link as revoked.
                if (flush?.isActive != true && !queue.isEmpty) flush = launch { runFlush() }
            }
            saveIfDue()
        }
    }

    /** Runs [flushQueue]. A 401 and cancellation propagate; other failures are logged (the queue keeps its items). */
    private suspend fun runFlush() {
        try {
            flushQueue()
        } catch (e: CancellationException) {
            throw e
        } catch (e: ApiException.Unauthorized) {
            throw e
        } catch (e: Exception) {
            report("Could not send the queued clips", e)
        }
    }

    /** Fetches an image clip's thumbnail in the background, at most [MAX_THUMBNAIL_FETCHES] at a time. */
    private fun requestThumbnail(clip: ClipDto) {
        if (clip.kind != ClipKind.IMAGE) return
        val ws = workers ?: return
        if (!thumbsInFlight.add(clip.hash)) return
        ws.launch {
            try {
                thumbSlots.withPermit {
                    if (cache.items.value.none { it.hash == clip.hash }) return@withPermit   // gone meanwhile
                    if (thumbs.get(clip.hash) != null) return@withPermit
                    thumbs.put(clip.hash, api.thumbnail(clip.id))
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: ApiException.Unauthorized) {
                requestRevoke()
            } catch (e: Exception) {
                report("Could not fetch a thumbnail", e)
            } finally {
                thumbsInFlight.remove(clip.hash)
            }
        }
    }

    /** A 401 outside the connection's own calls: end the attempt (or the wait) as revoked. */
    private fun requestRevoke() {
        revokeRequested.set(true)
        attempt?.cancel()
        wake.trySend(Unit)
    }

    private fun signOut() {
        if (!signedOut.compareAndSet(false, true)) return
        _state.value = SyncState.SIGNED_OUT
        revokedSignal.complete(Unit)
    }

    private fun markDirty() = synchronized(saveLock) { dirty = true }

    private fun saveIfDue() = synchronized(saveLock) {
        if (!dirty) return
        val now = clock.instant()
        val last = lastSaveAt
        if (last != null && elapsedMs(last, now) < SAVE_INTERVAL_MS) return
        saveLocked(now)
    }

    private fun saveIfDirty() = synchronized(saveLock) {
        if (dirty) saveLocked(clock.instant())
    }

    // Caller holds saveLock. HistoryCache.save logs its own write failures.
    private fun saveLocked(now: Instant) {
        lastSaveAt = now
        dirty = false
        cache.save()
        pruneThumbnails()
    }

    private fun pruneThumbnails() {
        try {
            thumbs.prune(cache.items.value.mapTo(HashSet()) { it.hash })
        } catch (e: SecurityException) {
            report("Could not prune thumbnails", e)
        }
    }

    private fun report(message: String, e: Throwable) {
        // Never the exception message: it can carry server text; clip contents never reach here.
        Log.w(TAG, "$message (${e.javaClass.simpleName})")
    }

    /** Ping/pong state shared by the receive loop and the monitor. */
    private class Pings(private var lastPingAt: Instant) {
        enum class Due { NONE, SEND, TIMEOUT }

        private var sentAt: Instant? = null
        private var busy = false

        @Synchronized
        fun check(now: Instant): Due {
            val sent = sentAt
            if (busy) {
                if (sent != null) sentAt = now   // the pong timeout restarts once the loop reads again
                return Due.NONE
            }
            if (sent != null) return if (elapsedMs(sent, now) >= PONG_TIMEOUT_MS) Due.TIMEOUT else Due.NONE
            if (elapsedMs(lastPingAt, now) < PING_INTERVAL_MS) return Due.NONE
            sentAt = now
            lastPingAt = now
            return Due.SEND
        }

        @Synchronized
        fun pong() {
            sentAt = null
        }

        @Synchronized
        fun setBusy(value: Boolean) {
            busy = value
        }
    }

    private companion object {
        const val TAG = "SyncEngine"
        const val PING = """{"type":"ping"}"""
        const val NORMAL_CLOSURE = 1000
        const val TICK_MS = 1_000L
        const val PING_INTERVAL_MS = 25_000L
        const val PONG_TIMEOUT_MS = 10_000L
        const val FLUSH_INTERVAL_MS = 30_000L
        const val SAVE_INTERVAL_MS = 2_000L
        const val MAX_THUMBNAIL_FETCHES = 2

        fun elapsedMs(from: Instant, to: Instant): Long = java.time.Duration.between(from, to).toMillis()
    }
}
