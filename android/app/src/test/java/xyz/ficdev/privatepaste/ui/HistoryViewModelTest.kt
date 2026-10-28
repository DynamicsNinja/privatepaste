package xyz.ficdev.privatepaste.ui

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ClipPage
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.ui.history.ClipCopier
import xyz.ficdev.privatepaste.ui.history.ClipRemote
import xyz.ficdev.privatepaste.ui.history.HistoryViewModel
import java.io.IOException
import java.time.Instant

@OptIn(ExperimentalCoroutinesApi::class)
class HistoryViewModelTest {

    private val dispatcher = StandardTestDispatcher()
    private val base = Instant.parse("2026-10-28T10:00:00Z")

    /** Everything the fakes see, in order: "copyText:..", "copyImage:id", "restore:id", "clips:q|cursor", ... */
    private val events = mutableListOf<String>()

    private val cache = MutableStateFlow<List<ClipDto>>(emptyList())
    private val sync = MutableStateFlow(SyncState.CONNECTED)
    private var appliedHash: String? = null
    private val localImages = HashMap<String, ByteArray>()
    private val remote = FakeRemote()

    private var failImageWrite = false
    private var unauthorizedCalls = 0

    private val copier = object : ClipCopier {
        override fun writeText(text: String) { events += "copyText:$text" }
        override fun writeImage(clipId: String, png: ByteArray) {
            events += "copyImage:$clipId:${png.size}"
            if (failImageWrite) throw IOException("disk full")
        }
    }

    @Before fun setUp() = Dispatchers.setMain(dispatcher)
    @After fun tearDown() = Dispatchers.resetMain()

    private fun clip(id: String, minute: Long, text: String? = "text $id", kind: ClipKind = ClipKind.TEXT) = ClipDto(
        id = id, seq = minute, kind = kind, text = text, hash = "h-$id", sizeBytes = 10,
        sourceDeviceId = "pc", sourceDeviceName = "Desk", lastCopiedAt = base.plusSeconds(minute * 60),
    )

    private fun vm(online: Boolean = true) = HistoryViewModel(
        cached = cache,
        lastAppliedHash = { appliedHash },
        syncState = sync,
        remote = { if (online) remote else null },
        copier = copier,
        cachedImage = { localImages[it] },
        io = dispatcher,
        onUnauthorized = { unauthorizedCalls++ },
    )

    /** Keeps [HistoryViewModel.state] collected, like the screen does. */
    private fun TestScope.collecting(vm: HistoryViewModel): HistoryViewModel {
        backgroundScope.launch { vm.state.collect {} }
        return vm
    }

    /** Collects toasts as they are sent (unconfined: background collectors are not run by advanceUntilIdle alone). */
    private fun TestScope.messagesOf(vm: HistoryViewModel): List<UiText> {
        val messages = mutableListOf<UiText>()
        backgroundScope.launch(UnconfinedTestDispatcher(testScheduler)) { vm.messages.collect { messages += it } }
        return messages
    }

    private fun HistoryViewModel.ids() = state.value.items.map { it.id }

    @Test
    fun cacheOnlyListArrivesWithoutANetworkCall() = runTest(dispatcher) {
        cache.value = listOf(clip("b", 2), clip("a", 1))
        val vm = collecting(vm())
        advanceUntilIdle()
        assertEquals(listOf("b", "a"), vm.ids())

        cache.value = listOf(clip("c", 3), clip("b", 2), clip("a", 1))
        advanceUntilIdle()
        assertEquals(listOf("c", "b", "a"), vm.ids())
        assertEquals(emptyList<String>(), events)
    }

    @Test
    fun liveDotMarksTheNewestClipWhenAppliedHere() = runTest(dispatcher) {
        cache.value = listOf(clip("b", 2), clip("a", 1))
        appliedHash = "h-b"
        val vm = collecting(vm())
        advanceUntilIdle()
        assertEquals("b", vm.state.value.liveClipId)

        cache.value = listOf(clip("c", 3)) + cache.value   // a newer clip from another device: no dot
        advanceUntilIdle()
        assertNull(vm.state.value.liveClipId)
    }

    @Test
    fun searchMergesCachedHitsFirstAndDedupes() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 3, "Hello world"), clip("b", 2, "other"), clip("c", 1, "say HELLO"))
        remote.pages["hello|"] = ClipPage(listOf(clip("x", 0, "hello from the server"), clip("a", 3, "Hello world")), null)
        val vm = collecting(vm())

        vm.onQueryChange("hello")
        advanceTimeBy(299)
        runCurrent()
        assertEquals(listOf("a", "c"), vm.ids())   // cached hits show at once
        assertTrue(events.isEmpty())

        advanceTimeBy(2)
        advanceUntilIdle()
        assertEquals(listOf("clips:hello|"), events)
        assertEquals(listOf("a", "c", "x"), vm.ids())
    }

    @Test
    fun searchIsDebouncedAndNeedsTwoCharacters() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 1, "hello"))
        val vm = collecting(vm())

        vm.onQueryChange("h")
        advanceUntilIdle()
        assertEquals(listOf("a"), vm.ids())
        assertTrue(events.isEmpty())

        vm.onQueryChange("he")
        advanceTimeBy(100)
        vm.onQueryChange("hel")
        advanceTimeBy(100)
        vm.onQueryChange("hell")
        advanceUntilIdle()
        assertEquals(listOf("clips:hell|"), events)
    }

    @Test
    fun pagingAppends() = runTest(dispatcher) {
        cache.value = listOf(clip("b", 20), clip("a", 19))
        remote.pages["|"] = ClipPage(listOf(clip("b", 20), clip("a", 19), clip("p1", 18)), "c1")
        remote.pages["|c1"] = ClipPage(listOf(clip("p2", 17), clip("p3", 16)), null)
        val vm = collecting(vm())
        advanceUntilIdle()

        vm.onNearEnd()
        advanceUntilIdle()
        assertEquals(listOf("b", "a", "p1"), vm.ids())

        vm.onNearEnd()
        advanceUntilIdle()
        assertEquals(listOf("b", "a", "p1", "p2", "p3"), vm.ids())

        vm.onNearEnd()   // no next cursor: nothing more to load
        advanceUntilIdle()
        assertEquals(listOf("clips:|", "clips:|c1"), events)
    }

    @Test
    fun searchResultsPageWithTheirCursor() = runTest(dispatcher) {
        remote.pages["ab|"] = ClipPage(listOf(clip("x1", 5, "ab 1")), "n1")
        remote.pages["ab|n1"] = ClipPage(listOf(clip("x2", 4, "ab 2")), null)
        val vm = collecting(vm())
        vm.onQueryChange("ab")
        advanceUntilIdle()
        vm.onNearEnd()
        advanceUntilIdle()
        assertEquals(listOf("x1", "x2"), vm.ids())
        assertEquals(listOf("clips:ab|", "clips:ab|n1"), events)
    }

    @Test
    fun tapCopiesBeforeRestore() = runTest(dispatcher) {
        val text = clip("a", 1, "secret")
        cache.value = listOf(text)
        val vm = collecting(vm())
        val messages = messagesOf(vm)

        vm.copy(text)
        advanceUntilIdle()
        assertEquals(listOf("copyText:secret", "restore:a"), events)
        assertEquals(listOf<UiText>(UiText.Res(R.string.history_copied)), messages)
    }

    @Test
    fun tapOnAnImageFetchesItWhenNotCachedThenRestores() = runTest(dispatcher) {
        val image = clip("img", 1, text = null, kind = ClipKind.IMAGE)
        remote.images["img"] = ByteArray(7)
        val vm = collecting(vm())
        vm.copy(image)
        advanceUntilIdle()
        assertEquals(listOf("image:img", "copyImage:img:7", "restore:img"), events)
    }

    @Test
    fun restoreFailureIsSwallowed() = runTest(dispatcher) {
        remote.failRestore = true
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        vm.copy(clip("a", 1, "x"))
        advanceUntilIdle()
        assertEquals(listOf("copyText:x", "restore:a"), events)
        assertEquals(listOf<UiText>(UiText.Res(R.string.history_copied)), messages)
    }

    @Test
    fun tapOfflineCopiesOnly() = runTest(dispatcher) {
        sync.value = SyncState.OFFLINE
        localImages["img2"] = ByteArray(3)
        val vm = collecting(vm())
        val messages = messagesOf(vm)

        vm.copy(clip("a", 1, "offline text"))
        advanceUntilIdle()
        vm.copy(clip("img2", 2, text = null, kind = ClipKind.IMAGE))   // cached full image
        advanceUntilIdle()
        vm.copy(clip("img", 3, text = null, kind = ClipKind.IMAGE))    // nothing cached
        advanceUntilIdle()

        assertEquals(listOf("copyText:offline text", "copyImage:img2:3"), events)
        assertEquals(
            listOf(
                UiText.Res(R.string.history_copied),
                UiText.Res(R.string.history_copied),
                UiText.Res(R.string.history_image_offline),
            ),
            messages,
        )
    }

    @Test
    fun tapWithoutAnApiCopiesOnly() = runTest(dispatcher) {
        val vm = collecting(vm(online = false))
        vm.copy(clip("a", 1, "x"))
        advanceUntilIdle()
        assertEquals(listOf("copyText:x"), events)
    }

    @Test
    fun deleteRemovesTheRow() = runTest(dispatcher) {
        cache.value = listOf(clip("b", 2), clip("a", 1))
        val vm = collecting(vm())
        advanceUntilIdle()

        vm.delete(cache.value[0])
        advanceUntilIdle()
        assertEquals(listOf("delete:b"), events)
        assertEquals(listOf("a"), vm.ids())

        // The cache still has it until the server's broadcast reaches the engine; the row stays gone.
        cache.value = listOf(clip("c", 3)) + cache.value
        advanceUntilIdle()
        assertEquals(listOf("c", "a"), vm.ids())
    }

    @Test
    fun failedDeleteKeepsTheRowAndSaysSo() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 1))
        remote.failDelete = true
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        advanceUntilIdle()

        vm.delete(cache.value[0])
        advanceUntilIdle()
        assertEquals(listOf("a"), vm.ids())
        assertEquals(listOf("delete:a"), events)
        assertEquals(listOf<UiText>(UiText.Res(R.string.history_delete_failed)), messages)
    }

    @Test
    fun unauthorizedDeleteRoutesToTheRevokePath() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 1))
        remote.revoked = true
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        advanceUntilIdle()

        vm.delete(cache.value[0])
        advanceUntilIdle()
        assertEquals(1, unauthorizedCalls)
        assertEquals("not a connection error", emptyList<UiText>(), messages)
    }

    @Test
    fun unauthorizedRestoreRoutesToTheRevokePath() = runTest(dispatcher) {
        remote.revoked = true
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        vm.copy(clip("a", 1, "x"))
        advanceUntilIdle()
        assertEquals(listOf("copyText:x", "restore:a"), events)
        assertEquals(listOf<UiText>(UiText.Res(R.string.history_copied)), messages)   // the local copy happened
        assertEquals(1, unauthorizedCalls)
    }

    @Test
    fun staleSearchResponseIsDropped() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 9, "abc cached"))
        val gate = CompletableDeferred<Unit>()
        remote.gates["ab|"] = gate
        remote.pages["ab|"] = ClipPage(listOf(clip("old", 5, "ab from the server")), null)
        remote.pages["abc|"] = ClipPage(listOf(clip("new", 4, "abc from the server")), null)
        val vm = collecting(vm())

        vm.onQueryChange("ab")
        advanceTimeBy(301)
        runCurrent()
        assertEquals(listOf("clips:ab|"), events)   // in flight, suspended

        vm.onQueryChange("abc")
        advanceTimeBy(100)
        gate.complete(Unit)                          // the "ab" answer arrives after the query moved on
        runCurrent()
        assertEquals(listOf("a"), vm.ids())

        advanceUntilIdle()
        assertEquals(listOf("clips:ab|", "clips:abc|"), events)
        assertEquals(listOf("a", "new"), vm.ids())
    }

    @Test
    fun imageWriteFailureSaysCouldNotCopy() = runTest(dispatcher) {
        failImageWrite = true
        localImages["img"] = ByteArray(4)
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        vm.copy(clip("img", 1, text = null, kind = ClipKind.IMAGE))
        advanceUntilIdle()
        assertEquals(listOf("copyImage:img:4"), events)   // no restore after a failed local copy
        assertEquals(listOf<UiText>(UiText.Res(R.string.history_copy_failed)), messages)
    }

    @Test
    fun textClipWithoutTextIsNotCopied() = runTest(dispatcher) {
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        vm.copy(clip("t", 1, text = null))
        vm.copy(clip("l", 2, text = null, kind = ClipKind.LINK))
        advanceUntilIdle()
        assertEquals(emptyList<String>(), events)
        assertEquals(emptyList<UiText>(), messages)
    }

    private inner class FakeRemote : ClipRemote {
        val pages = HashMap<String, ClipPage>()
        /** A response for a "q|cursor" key waits until its gate completes. */
        val gates = HashMap<String, CompletableDeferred<Unit>>()
        val images = HashMap<String, ByteArray>()
        var failRestore = false
        var failDelete = false
        /** Every call throws 401: this device was revoked. */
        var revoked = false

        override suspend fun clips(q: String?, cursor: String?, limit: Int): ClipPage {
            val key = "${q.orEmpty()}|${cursor.orEmpty()}"
            events += "clips:$key"
            gates[key]?.await()
            return pages[key] ?: ClipPage(emptyList(), null)
        }

        override suspend fun restore(id: String): ClipDto? {
            events += "restore:$id"
            if (revoked) throw ApiException.Unauthorized()
            if (failRestore) throw ApiException.Transient("down")
            return null
        }

        override suspend fun delete(id: String): Boolean {
            events += "delete:$id"
            if (revoked) throw ApiException.Unauthorized()
            if (failDelete) throw ApiException.Transient("down")
            return true
        }

        override suspend fun thumbnail(id: String): ByteArray {
            events += "thumbnail:$id"
            throw ApiException.Rejected(404, "none")
        }

        override suspend fun image(id: String): ByteArray {
            events += "image:$id"
            return images[id] ?: throw ApiException.Rejected(404, "none")
        }
    }
}
