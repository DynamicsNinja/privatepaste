package xyz.ficdev.privatepaste.sync

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.consumeAsFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.TestCoroutineScheduler
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.SyncPage
import xyz.ficdev.privatepaste.core.model.WsEvent
import xyz.ficdev.privatepaste.storage.CryptoFile
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.InMemoryKeyProvider
import xyz.ficdev.privatepaste.storage.KeyAliases
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import java.io.File
import java.io.IOException
import java.time.Clock
import java.time.Instant
import java.time.ZoneId
import java.time.ZoneOffset
import kotlin.random.Random

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
class SyncEngineTest {

    @get:Rule val tmp = TemporaryFolder()

    private val keys = InMemoryKeyProvider()
    private val base = Instant.parse("2026-10-28T10:00:00Z")
    private val own = "own-device"
    private val other = "other-device"

    private lateinit var cache: HistoryCache
    private lateinit var queue: OutboundQueue
    private lateinit var thumbs: ThumbnailStore
    private val api = FakeApi()
    private val links = FakeLinks()
    private val capture = LocalCaptureClock()
    private val applied = mutableListOf<ClipDto>()
    private var applyResult = true
    private var flushes = 0
    private var flushError: Exception? = null
    private var flushGate: CompletableDeferred<Unit>? = null
    private var flushesInFlight = 0
    private var maxFlushesInFlight = 0

    @Before
    fun setUp() {
        cache = HistoryCache(CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE))
        queue = OutboundQueue(CryptoFile(File(tmp.root, "queue.bin"), keys, KeyAliases.QUEUE), tmp.newFolder("queue-images"), keys)
        thumbs = ThumbnailStore(tmp.newFolder("thumbs"), keys)
    }

    // ---------------------------------------------------------------- fakes

    private class FakeApi : SyncApi {
        val pages = ArrayDeque<SyncPage>()
        val syncCalls = mutableListOf<Long>()
        var syncError: Exception? = null
        var syncGate: CompletableDeferred<Unit>? = null
        val thumbCalls = mutableListOf<String>()
        var thumbGate: CompletableDeferred<Unit>? = null
        var thumbError: Exception? = null

        override suspend fun sync(afterSeq: Long, limit: Int): SyncPage {
            syncCalls += afterSeq
            syncGate?.await()
            syncError?.let { throw it }
            return pages.removeFirstOrNull() ?: SyncPage(emptyList(), emptyList(), afterSeq, false)
        }

        override suspend fun thumbnail(id: String): ByteArray {
            thumbCalls += id
            thumbGate?.await()
            thumbError?.let { throw it }
            return "png-$id".toByteArray()
        }
    }

    private class FakeLink(private val autoPong: Boolean) : WsLink {
        private val incoming = Channel<WsEvent>(Channel.UNLIMITED)
        val sent = mutableListOf<String>()
        var closed = false

        override val events: Flow<WsEvent> = incoming.consumeAsFlow()

        override fun send(text: String): Boolean {
            if (closed) return false
            sent += text
            if (autoPong && text == """{"type":"ping"}""") incoming.trySend(WsEvent.Pong)
            return true
        }

        override fun close(code: Int, reason: String) {
            closed = true
            incoming.close()
        }

        fun push(event: WsEvent) = check(incoming.trySend(event).isSuccess)

        fun serverClose(code: Int) {
            incoming.close(if (code == 1008) RevokedException() else null)
        }
    }

    private class FakeLinks : LinkFactory {
        val handedOut = mutableListOf<FakeLink>()
        var connects = 0
        var failure: Exception? = null
        var autoPong = true
        var connectGate: CompletableDeferred<Unit>? = null

        /** The gate ignores cancellation: a connect that is slow to notice a stop. */
        var stubborn = false

        val last: FakeLink get() = handedOut.last()

        override suspend fun connect(): WsLink {
            connects++
            connectGate?.let { gate -> if (stubborn) withContext(NonCancellable) { gate.await() } else gate.await() }
            failure?.let { throw it }
            return FakeLink(autoPong).also { handedOut += it }
        }
    }

    /** A java.time clock that follows the coroutine test scheduler's virtual time. */
    private class VirtualClock(private val scheduler: TestCoroutineScheduler, private val start: Instant) : Clock() {
        override fun getZone(): ZoneId = ZoneOffset.UTC
        override fun withZone(zone: ZoneId?): Clock = this
        override fun instant(): Instant = start.plusMillis(scheduler.currentTime)
    }

    /** nextDouble 0.5 = jitter factor exactly 1, so delays are 1, 2, 4 ... s. */
    private object NoJitter : Random() {
        override fun nextBits(bitCount: Int): Int = 0
        override fun nextDouble(): Double = 0.5
    }

    private fun TestScope.engine(): SyncEngine = SyncEngine(
        api = api,
        cache = cache,
        queue = queue,
        thumbs = thumbs,
        applier = { clip -> applied += clip; applyResult },
        flushQueue = {
            flushes++
            flushesInFlight++
            maxFlushesInFlight = maxOf(maxFlushesInFlight, flushesInFlight)
            try {
                flushGate?.await()
                flushError?.let { throw it }
            } finally {
                flushesInFlight--
            }
        },
        links = links,
        ownDeviceId = own,
        capture = capture,
        clock = VirtualClock(testScheduler, base),
        backoff = Backoff(NoJitter),
        scope = backgroundScope,
    )

    private fun clip(
        id: String,
        seq: Long,
        minute: Long = seq,
        device: String = other,
        kind: ClipKind = ClipKind.TEXT,
        hash: String = "h-$id-$seq",
    ) = ClipDto(
        id = id, seq = seq, kind = kind, text = if (kind == ClipKind.IMAGE) null else "text $id",
        hash = hash, width = if (kind == ClipKind.IMAGE) 10 else null, height = if (kind == ClipKind.IMAGE) 10 else null,
        sizeBytes = 6, sourceDeviceId = device, lastCopiedAt = base.plusSeconds(minute * 60),
    )

    private fun page(vararg clips: ClipDto, deleted: List<String> = emptyList(), latest: Long, hasMore: Boolean = false) =
        SyncPage(clips.toList(), deleted, latest, hasMore)

    // ---------------------------------------------------------------- catch-up

    @Test
    fun catchUpAppliesOnlyNewest() = runTest {
        cache.markApplied(base, "previous")
        api.pages += page(clip("a", 1), clip("b", 2), latest = 4, hasMore = true)
        api.pages += page(clip("c", 3), clip("mine", 4, minute = 0, device = own), latest = 4)
        val engine = engine()

        engine.start()
        runCurrent()

        assertEquals(listOf("c"), applied.map { it.id })
        assertEquals(clip("c", 3).lastCopiedAt, cache.lastAppliedAt)
        assertEquals(clip("c", 3).hash, cache.lastAppliedHash)
        // A truncated page continues after its last clip, never from its (global) latestSeq.
        assertEquals(listOf(0L, 2L), api.syncCalls)
        assertEquals(4L, cache.highestSeq)
        assertEquals(4, cache.items.value.size)
        assertEquals(1, flushes)
        assertEquals(SyncState.CONNECTED, engine.state.value)
    }

    @Test
    fun catchUpNotMarkedWhenApplierDeclines() = runTest {
        cache.markApplied(base, "previous")
        applyResult = false
        api.pages += page(clip("a", 1), latest = 1)
        engine().start()
        runCurrent()

        assertEquals(listOf("a"), applied.map { it.id })
        assertEquals(base, cache.lastAppliedAt)
    }

    @Test
    fun firstSyncAfterSignInIsHistoryOnly() = runTest {
        api.pages += page(clip("a", 1), clip("b", 2), latest = 2)
        val engine = engine()

        engine.start()
        runCurrent()

        assertTrue(applied.isEmpty())
        assertNull(cache.lastAppliedAt)
        assertEquals(listOf("b", "a"), cache.items.value.map { it.id })
        assertEquals(SyncState.CONNECTED, engine.state.value)
    }

    @Test
    fun catchUpSkippedWhenQueueNotEmpty() = runTest {
        cache.markApplied(base, "previous")
        queue.enqueueText("copied offline", base.plusSeconds(30))
        api.pages += page(clip("a", 1, minute = 5), latest = 1)

        engine().start()
        runCurrent()

        assertTrue(applied.isEmpty())
        assertEquals(listOf("a"), cache.items.value.map { it.id })
        assertEquals(1, flushes)
    }

    @Test
    fun catchUpSkippedWhenLocalSendIsNewer() = runTest {
        cache.markApplied(base, "previous")
        capture.record(base.plusSeconds(6 * 60))
        api.pages += page(clip("a", 1, minute = 5), latest = 1)

        engine().start()
        runCurrent()

        assertTrue(applied.isEmpty())
        assertEquals(listOf("a"), cache.items.value.map { it.id })
    }

    @Test
    fun catchUpSkippedWhenNewestIsNotTopOfHistory() = runTest {
        cache.markApplied(base, "previous")
        cache.upsert(clip("mine", 1, minute = 10, device = own), fromSync = true)
        api.pages += page(clip("a", 2, minute = 5), latest = 2)

        engine().start()
        runCurrent()

        assertTrue(applied.isEmpty())
    }

    // ---------------------------------------------------------------- live events

    @Test
    fun liveClipFromOtherDeviceIsApplied() = runTest {
        val engine = engine()
        engine.start()
        runCurrent()

        val x = clip("x", 1)
        links.last.push(WsEvent.ClipCreated(x, live = true))
        runCurrent()

        assertEquals(listOf(x), applied)
        assertEquals(x.lastCopiedAt, cache.lastAppliedAt)
        assertEquals(x.hash, cache.lastAppliedHash)
        assertEquals(listOf("x"), cache.items.value.map { it.id })
        assertEquals(1L, cache.highestSeq)

        // Declined (e.g. notification not posted): in history, but not marked as applied.
        applyResult = false
        val y = clip("y", 2)
        links.last.push(WsEvent.ClipCreated(y, live = true))
        runCurrent()

        assertEquals(listOf(x, y), applied)
        assertEquals(x.lastCopiedAt, cache.lastAppliedAt)
        assertEquals(listOf("y", "x"), cache.items.value.map { it.id })
    }

    @Test
    fun backfillAndOwnClipsAreNotApplied() = runTest {
        engine().start()
        runCurrent()

        links.last.push(WsEvent.ClipCreated(clip("backfill", 1), live = false))
        links.last.push(WsEvent.ClipCreated(clip("mine", 2, device = own), live = true))
        runCurrent()

        assertTrue(applied.isEmpty())
        assertEquals(listOf("mine", "backfill"), cache.items.value.map { it.id })
        assertNull(cache.lastAppliedAt)
    }

    @Test
    fun deleteRemovesFromCache() = runTest {
        val a = clip("a", 1, kind = ClipKind.IMAGE, hash = "hash-a")
        api.pages += page(a, clip("b", 2), latest = 2)
        thumbs.put("hash-a", byteArrayOf(9))
        thumbs.put("orphan", byteArrayOf(9))
        engine().start()
        runCurrent()
        assertNull("pruned on start", thumbs.get("orphan"))

        links.last.push(WsEvent.ClipDeleted("a", 9))
        runCurrent()

        assertEquals(listOf("b"), cache.items.value.map { it.id })
        assertEquals(9L, cache.highestSeq)

        advanceTimeBy(3_000)
        runCurrent()
        assertNull("thumbnail of the deleted clip is pruned", thumbs.get("hash-a"))
        // Persisted: a fresh load sees the deletion and the position.
        val reloaded = HistoryCache(CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE))
        assertEquals(listOf("b"), reloaded.items.value.map { it.id })
        assertEquals(9L, reloaded.highestSeq)
    }

    @Test
    fun outOfOrderBroadcastsKeepNewestSeq() = runTest {
        engine().start()
        runCurrent()

        val newest = clip("x", 5)
        links.last.push(WsEvent.ClipCreated(newest, live = true))
        links.last.push(WsEvent.ClipCreated(clip("x", 3), live = true))   // older version of x
        links.last.push(WsEvent.ClipCreated(clip("y", 4), live = true))   // older clip, arrives late
        runCurrent()

        assertEquals(listOf(newest), applied)
        assertEquals(5L, cache.highestSeq)
        assertEquals(5L, cache.items.value.first { it.id == "x" }.seq)
        assertEquals(newest.lastCopiedAt, cache.lastAppliedAt)
    }

    // ---------------------------------------------------------------- revocation

    @Test
    fun close1008StopsForGood() = runTest {
        val engine = engine()
        val revoked = backgroundScope.async { engine.revoked.first() }
        engine.start()
        runCurrent()
        assertEquals(SyncState.CONNECTED, engine.state.value)

        links.last.serverClose(1008)
        runCurrent()

        assertTrue(revoked.isCompleted)
        assertEquals(SyncState.SIGNED_OUT, engine.state.value)
        advanceTimeBy(10 * 60_000)
        engine.start()
        advanceTimeBy(10 * 60_000)
        assertEquals(1, links.connects)
        assertEquals(SyncState.SIGNED_OUT, engine.state.value)
        // A late collector still gets the one emission.
        engine.revoked.first()
    }

    @Test
    fun close1001ReconnectsWithBackoff() = runTest {
        val engine = engine()
        engine.start()
        runCurrent()

        links.last.serverClose(1001)
        runCurrent()
        assertEquals(SyncState.OFFLINE, engine.state.value)
        advanceTimeBy(1_001)
        assertEquals(2, links.connects)
        assertEquals(SyncState.CONNECTED, engine.state.value)
    }

    @Test
    fun http401StopsForGood() = runTest {
        api.syncError = ApiException.Unauthorized()
        val engine = engine()
        val revoked = backgroundScope.async { engine.revoked.first() }
        engine.start()
        runCurrent()

        assertTrue(revoked.isCompleted)
        assertEquals(SyncState.SIGNED_OUT, engine.state.value)
        advanceTimeBy(10 * 60_000)
        assertEquals(1, links.connects)
        assertEquals(1, api.syncCalls.size)
    }

    @Test
    fun http401OnConnectOrFlushStopsForGood() = runTest {
        links.failure = ApiException.Unauthorized()
        val first = engine()
        first.start()
        runCurrent()
        assertEquals(SyncState.SIGNED_OUT, first.state.value)
        first.revoked.first()

        links.failure = null
        flushError = ApiException.Unauthorized()
        val second = engine()
        second.start()
        runCurrent()
        assertEquals(SyncState.SIGNED_OUT, second.state.value)
        second.revoked.first()
        advanceTimeBy(10 * 60_000)
        assertEquals(2, links.connects)
    }

    // ---------------------------------------------------------------- liveness and reconnect

    @Test
    fun pongTimeoutReconnectsWithBackoff() = runTest {
        links.autoPong = false
        val engine = engine()
        engine.start()
        runCurrent()
        val link = links.last

        advanceTimeBy(24_999)
        assertTrue(link.sent.isEmpty())
        advanceTimeBy(1)
        runCurrent()
        assertEquals(listOf("""{"type":"ping"}"""), link.sent)

        advanceTimeBy(9_999)
        assertFalse(link.closed)
        assertEquals(SyncState.CONNECTED, engine.state.value)
        advanceTimeBy(1)
        runCurrent()
        assertTrue(link.closed)
        assertEquals(SyncState.OFFLINE, engine.state.value)

        // The link was up 35 s (>= 25 s), so the backoff starts over at 1 s, then grows while connects fail.
        links.failure = IOException("offline")
        advanceTimeBy(999)
        assertEquals(1, links.connects)
        advanceTimeBy(1)
        runCurrent()
        assertEquals(2, links.connects)
        advanceTimeBy(1_999)
        assertEquals(2, links.connects)
        advanceTimeBy(1)
        runCurrent()
        assertEquals(3, links.connects)
        advanceTimeBy(3_999)
        assertEquals(3, links.connects)
        advanceTimeBy(1)
        runCurrent()
        assertEquals(4, links.connects)
    }

    @Test
    fun shortLivedLinkDoesNotResetBackoff() = runTest {
        val engine = engine()
        engine.start()
        runCurrent()
        // Fails at once twice: 1 s, then 2 s.
        links.last.serverClose(1001)
        advanceTimeBy(1_000)
        runCurrent()
        assertEquals(2, links.connects)
        advanceTimeBy(5_000)   // up only 5 s
        links.last.serverClose(1001)
        runCurrent()
        advanceTimeBy(1_999)
        assertEquals(2, links.connects)
        advanceTimeBy(1)
        runCurrent()
        assertEquals(3, links.connects)
        assertEquals(SyncState.CONNECTED, engine.state.value)
    }

    @Test
    fun nudgeReconnectsImmediately() = runTest {
        links.failure = IOException("offline")
        val engine = engine()
        engine.start()
        runCurrent()
        advanceTimeBy(1_000)
        runCurrent()
        advanceTimeBy(2_000)
        runCurrent()
        assertEquals(3, links.connects)   // now waiting 4 s

        engine.nudge()
        runCurrent()
        assertEquals(4, links.connects)
        // The backoff was reset: the next failure waits 1 s again.
        advanceTimeBy(999)
        assertEquals(4, links.connects)
        advanceTimeBy(1)
        runCurrent()
        assertEquals(5, links.connects)

        // While connected, a nudge drops the link and reconnects at once.
        links.failure = null
        engine.nudge()
        runCurrent()
        assertEquals(6, links.connects)
        assertEquals(SyncState.CONNECTED, engine.state.value)
        val link = links.last
        engine.nudge()
        runCurrent()
        assertTrue(link.closed)
        assertEquals(7, links.connects)
        assertEquals(SyncState.CONNECTED, engine.state.value)
    }

    @Test
    fun stopEndsTheLinkAndSaves() = runTest {
        val engine = engine()
        engine.start()
        runCurrent()
        links.last.push(WsEvent.ClipCreated(clip("a", 1), live = false))
        links.last.push(WsEvent.ClipCreated(clip("b", 2), live = false))   // within the 2 s throttle
        runCurrent()

        engine.stop()

        assertTrue(links.last.closed)
        assertEquals(SyncState.OFFLINE, engine.state.value)
        val reloaded = HistoryCache(CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE))
        assertEquals(listOf("b", "a"), reloaded.items.value.map { it.id })
        advanceTimeBy(60_000)
        assertEquals(1, links.connects)

        engine.nudge()   // no effect while stopped
        engine.start()
        runCurrent()
        assertEquals(2, links.connects)
        assertEquals(SyncState.CONNECTED, engine.state.value)
    }

    @Test
    fun periodicFlushWhileConnected() = runTest {
        queue.enqueueText("waiting", base)
        engine().start()
        runCurrent()
        assertEquals(1, flushes)

        advanceTimeBy(30_000)
        runCurrent()
        assertEquals(2, flushes)
        advanceTimeBy(30_000)
        runCurrent()
        assertEquals(3, flushes)

        queue.clear()
        advanceTimeBy(90_000)
        runCurrent()
        assertEquals(3, flushes)
        assertEquals(1, links.connects)
    }

    // ---------------------------------------------------------------- thumbnails

    @Test
    fun thumbnailFetchedForImageClip() = runTest {
        engine().start()
        runCurrent()
        val gate = CompletableDeferred<Unit>()
        api.thumbGate = gate

        links.last.push(WsEvent.ClipCreated(clip("t", 1), live = false))
        for (i in 2..4) links.last.push(WsEvent.ClipCreated(clip("img$i", i.toLong(), kind = ClipKind.IMAGE, hash = "hash$i"), live = false))
        runCurrent()
        assertEquals("at most 2 at a time", listOf("img2", "img3"), api.thumbCalls)

        gate.complete(Unit)
        runCurrent()
        assertEquals(listOf("img2", "img3", "img4"), api.thumbCalls)
        for (i in 2..4) assertArrayEquals("png-img$i".toByteArray(), thumbs.get("hash$i"))

        // Already stored: not fetched again.
        links.last.push(WsEvent.ClipCreated(clip("img2", 9, kind = ClipKind.IMAGE, hash = "hash2"), live = false))
        runCurrent()
        assertEquals(3, api.thumbCalls.size)
    }

    @Test
    fun thumbnailFailureIsSwallowed() = runTest {
        api.thumbError = ApiException.Transient("boom")
        val engine = engine()
        engine.start()
        runCurrent()

        links.last.push(WsEvent.ClipCreated(clip("img", 1, kind = ClipKind.IMAGE, hash = "hashimg"), live = true))
        runCurrent()
        assertEquals(listOf("img"), api.thumbCalls)
        assertNull(thumbs.get("hashimg"))
        assertEquals(SyncState.CONNECTED, engine.state.value)

        links.last.push(WsEvent.ClipCreated(clip("next", 2), live = true))
        runCurrent()
        assertEquals(listOf("img", "next"), applied.map { it.id })
        assertEquals(1, links.connects)
    }

    // ---------------------------------------------------------------- fix round 1

    @Test
    fun liveClipAlreadyDeletedIsNotApplied() = runTest {
        engine().start()
        runCurrent()

        links.last.push(WsEvent.ClipDeleted("z", 5))
        links.last.push(WsEvent.ClipCreated(clip("z", 4), live = true))   // its delete overtook it
        runCurrent()

        assertTrue(applied.isEmpty())
        assertTrue(cache.items.value.isEmpty())
    }

    @Test
    fun liveClipOlderThanTopOfHistoryIsNotApplied() = runTest {
        engine().start()
        runCurrent()

        // A newer clip from this device, then a late broadcast of an older one from another device.
        links.last.push(WsEvent.ClipCreated(clip("mine", 2, minute = 10, device = own), live = true))
        links.last.push(WsEvent.ClipCreated(clip("old", 1, minute = 5), live = true))
        runCurrent()
        assertTrue(applied.isEmpty())

        // Declined apply (lastAppliedAt unchanged), then an older clip: still not applied.
        applyResult = false
        val top = clip("top", 3, minute = 20)
        links.last.push(WsEvent.ClipCreated(top, live = true))
        links.last.push(WsEvent.ClipCreated(clip("older", 4, minute = 15), live = true))
        runCurrent()
        assertEquals(listOf(top), applied)
    }

    @Test
    fun startWhileStoppingWaitsForTheOldRun() = runTest {
        val engine = engine()
        val gate = CompletableDeferred<Unit>()
        links.connectGate = gate
        links.stubborn = true
        engine.start()
        runCurrent()
        assertEquals(1, links.connects)

        var stopped = false
        backgroundScope.launch(start = CoroutineStart.UNDISPATCHED) { engine.stop(); stopped = true }
        engine.start()
        runCurrent()
        assertFalse(stopped)
        assertEquals("no second loop while the first still unwinds", 1, links.connects)

        links.connectGate = null
        gate.complete(Unit)
        runCurrent()
        assertTrue(stopped)
        assertEquals(2, links.connects)
        assertEquals(SyncState.CONNECTED, engine.state.value)

        // The new run owns the worker scope: thumbnails still load.
        links.last.push(WsEvent.ClipCreated(clip("img", 1, kind = ClipKind.IMAGE, hash = "hashimg"), live = false))
        runCurrent()
        assertEquals(listOf("img"), api.thumbCalls)
    }

    @Test
    fun concurrentStopWaitsForTheFirst() = runTest {
        val engine = engine()
        val gate = CompletableDeferred<Unit>()
        links.connectGate = gate
        links.stubborn = true
        engine.start()
        runCurrent()

        var first = false
        var second = false
        backgroundScope.launch(start = CoroutineStart.UNDISPATCHED) { engine.stop(); first = true }
        backgroundScope.launch(start = CoroutineStart.UNDISPATCHED) { engine.stop(); second = true }
        runCurrent()
        assertFalse(first)
        assertFalse("a second stop must not return before the first has finished", second)

        gate.complete(Unit)
        runCurrent()
        assertTrue(first)
        assertTrue(second)
        assertEquals(SyncState.OFFLINE, engine.state.value)
    }

    @Test
    fun nudgeDuringPendingConnectRetriesAtOnce() = runTest {
        val gate = CompletableDeferred<Unit>()
        links.connectGate = gate
        val engine = engine()
        engine.start()
        runCurrent()
        assertEquals(1, links.connects)
        assertEquals(SyncState.CONNECTING, engine.state.value)

        engine.nudge()
        runCurrent()
        assertEquals(2, links.connects)

        gate.complete(Unit)
        runCurrent()
        assertEquals(1, links.handedOut.size)   // the abandoned attempt never produced a link
        assertEquals(SyncState.CONNECTED, engine.state.value)
    }

    @Test
    fun stopDuringCatchUp() = runTest {
        api.syncGate = CompletableDeferred()
        api.pages += page(clip("a", 1), latest = 1)
        val engine = engine()
        engine.start()
        runCurrent()
        assertEquals(1, api.syncCalls.size)

        engine.stop()

        assertTrue(links.last.closed)
        assertEquals(SyncState.OFFLINE, engine.state.value)
        api.syncGate!!.complete(Unit)
        advanceTimeBy(60_000)
        assertTrue(cache.items.value.isEmpty())
        assertEquals(0, flushes)
        assertEquals(1, links.connects)
    }

    @Test
    fun periodicFlush401Revokes() = runTest {
        queue.enqueueText("waiting", base)
        val engine = engine()
        engine.start()
        runCurrent()
        assertEquals(1, flushes)

        flushError = ApiException.Unauthorized()
        advanceTimeBy(30_000)
        runCurrent()

        assertEquals(2, flushes)
        assertEquals(SyncState.SIGNED_OUT, engine.state.value)
        engine.revoked.first()
        assertTrue(links.last.closed)
        advanceTimeBy(10 * 60_000)
        assertEquals(1, links.connects)
    }

    @Test
    fun atMostOnePeriodicFlushInFlight() = runTest {
        queue.enqueueText("waiting", base)
        engine().start()
        runCurrent()
        assertEquals(1, flushes)

        val gate = CompletableDeferred<Unit>()
        flushGate = gate
        advanceTimeBy(30_000)
        runCurrent()
        assertEquals(2, flushes)
        advanceTimeBy(90_000)   // the slow flush still runs; pings keep the link up meanwhile
        runCurrent()
        assertEquals(2, flushes)
        assertEquals(1, maxFlushesInFlight)
        assertEquals(1, links.connects)

        flushGate = null
        gate.complete(Unit)
        advanceTimeBy(30_000)
        runCurrent()
        assertEquals(3, flushes)
    }
}
