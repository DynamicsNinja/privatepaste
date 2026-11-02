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
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ClipPage
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.core.model.ClipFileInfo
import xyz.ficdev.privatepaste.ui.history.ClipCopier
import xyz.ficdev.privatepaste.ui.history.ClipFileActions
import xyz.ficdev.privatepaste.ui.history.ClipRemote
import xyz.ficdev.privatepaste.ui.history.HistoryTab
import xyz.ficdev.privatepaste.ui.history.HistoryViewModel
import android.content.Intent
import java.io.IOException
import java.time.Instant

@OptIn(ExperimentalCoroutinesApi::class)
class HistoryViewModelTest {

    private val dispatcher = StandardTestDispatcher()
    private val base = Instant.parse("2026-10-28T10:00:00Z")

    /** Everything the fakes see, in order: "copyText:..", "copyImage:id", "restore:id", "clips:[P:]|cursor", ... */
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

    private var fileCopyWorks = true
    private var fileSaveWorks = true
    private var shareIntent: Intent? = Intent("share")
    private val files = object : ClipFileActions {
        override suspend fun copy(clipId: String): Boolean { events += "copyFiles:$clipId"; return fileCopyWorks }
        override suspend fun save(clipId: String): Boolean { events += "saveFiles:$clipId"; return fileSaveWorks }
        override suspend fun share(clipId: String): Intent? { events += "shareFiles:$clipId"; return shareIntent }
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
        files = files,
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

    private fun pinned(clip: ClipDto, pinnedMinute: Long) =
        clip.copy(pinnedAt = base.plusSeconds(pinnedMinute * 60).toString())

    @Test
    fun pinnedTabShowsOnlyPinsNewestPinnedFirstAndMergesTheServersList() = runTest(dispatcher) {
        cache.value = listOf(clip("c", 30), pinned(clip("b", 20), 5), clip("x", 10), pinned(clip("a", 1), 9))
        remote.pages["P:|"] = ClipPage(listOf(pinned(clip("old", 0), 2), pinned(clip("a", 1), 9)), null)
        val vm = collecting(vm())
        advanceUntilIdle()
        assertEquals(HistoryTab.HISTORY, vm.state.value.tab)
        assertEquals(listOf("c", "b", "x", "a"), vm.ids())

        vm.onTab(HistoryTab.PINNED)
        advanceUntilIdle()
        assertEquals(HistoryTab.PINNED, vm.state.value.tab)
        assertEquals(listOf("a", "b", "old"), vm.ids())
        assertEquals(listOf("clips:P:|"), events)

        vm.onTab(HistoryTab.HISTORY)
        advanceUntilIdle()
        assertEquals(listOf("c", "b", "x", "a"), vm.ids())
    }

    @Test
    fun searchFiltersThePinnedTab() = runTest(dispatcher) {
        cache.value = listOf(pinned(clip("a", 2, "alpha"), 3), pinned(clip("b", 1, "beta"), 4), clip("c", 5, "alpha too"))
        val vm = collecting(vm(online = false))
        vm.onTab(HistoryTab.PINNED)
        vm.onQueryChange("alp")
        advanceUntilIdle()
        assertEquals(listOf("a"), vm.ids())
    }

    @Test
    fun pinningUpdatesTheRowAtOnceAndUnpinningLeavesThePinnedTab() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 2), pinned(clip("b", 1), 4))
        val vm = collecting(vm())
        advanceUntilIdle()

        vm.togglePin(cache.value[0])
        advanceUntilIdle()
        assertEquals(listOf("pin:a:true"), events)
        assertTrue(vm.state.value.items.first { it.id == "a" }.pinnedAt != null)

        vm.onTab(HistoryTab.PINNED)
        advanceUntilIdle()
        assertEquals(listOf("a", "b"), vm.ids())

        events.clear()
        vm.togglePin(vm.state.value.items.first { it.id == "b" })
        advanceUntilIdle()
        assertTrue("pin:b:false" in events)
        assertEquals(listOf("a"), vm.ids())
    }

    @Test
    fun pinFailureToastsAndChangesNothing() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 1))
        remote.failPin = true
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        vm.togglePin(cache.value[0])
        advanceUntilIdle()
        assertEquals(listOf<UiText>(UiText.Res(R.string.history_pin_failed)), messages)
        assertNull(vm.state.value.items.single().pinnedAt)

        // Offline: no call at all.
        val offline = collecting(vm(online = false))
        val offlineMessages = messagesOf(offline)
        offline.togglePin(cache.value[0])
        advanceUntilIdle()
        assertEquals(listOf<UiText>(UiText.Res(R.string.history_pin_failed)), offlineMessages)
    }

    @Test
    fun pinningAClipDeletedElsewhereDropsTheRow() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 1))
        remote.pinNotFound = true
        val vm = collecting(vm())
        vm.togglePin(cache.value[0])
        advanceUntilIdle()
        assertEquals(emptyList<String>(), vm.ids())
    }

    @Test
    fun searchFiltersCachedAndLoadedClipsOnThisPhone() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 30, "Hello world"), clip("b", 20, "other"), clip("c", 10, "say HELLO"))
        remote.pages["|"] = ClipPage(listOf(clip("a", 30, "Hello world"), clip("x", 5, "hello from an older page"), clip("y", 4, "nope")), null)
        val vm = collecting(vm())
        vm.onNearEnd()
        advanceUntilIdle()
        assertEquals(listOf("a", "b", "c", "x", "y"), vm.ids())
        events.clear()

        vm.onQueryChange("hello")
        runCurrent()
        assertEquals(listOf("a", "c", "x"), vm.ids())   // case-insensitive, at once, deduped by id
        advanceUntilIdle()
        assertEquals("the server holds only ciphertext: search never asks it", emptyList<String>(), events)

        vm.onQueryChange("")
        advanceUntilIdle()
        assertEquals(listOf("a", "b", "c", "x", "y"), vm.ids())
    }

    @Test
    fun searchOlderAppendsMatches() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 30, "Hello"), clip("b", 20, "other"))
        remote.pages["|"] = ClipPage(listOf(clip("a", 30, "Hello"), clip("x", 5, "the INVOICE for May"), clip("y", 4, "nope")), "c1")
        remote.pages["|c1"] = ClipPage(listOf(clip("z", 3, "old invoice"), clip("w", 2, "nothing")), null)
        val vm = collecting(vm())
        vm.onQueryChange("invoice")
        advanceUntilIdle()
        assertEquals(emptyList<String>(), vm.ids())
        assertTrue("nothing here matches: offer the older clips", vm.state.value.canSearchOlder)
        assertEquals(emptyList<String>(), events)

        vm.searchOlder()
        advanceUntilIdle()
        assertEquals(listOf("x", "z"), vm.ids())   // every page, opened and filtered here, case-insensitive
        assertEquals(listOf("clips:|", "clips:|c1"), events)
        assertEquals(listOf(200, 200), remote.limits)
        assertEquals(false, vm.state.value.canSearchOlder)
        assertEquals(false, vm.state.value.searchingOlder)

        vm.onQueryChange("invoice for")   // a new query forgets the older results
        advanceUntilIdle()
        assertEquals(emptyList<String>(), vm.ids())
        assertTrue(vm.state.value.canSearchOlder)
    }

    @Test
    fun searchOlderThatFailsPartWayKeepsItsHitsAndOffersARetry() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 30, "Hello"))
        remote.pages["|"] = ClipPage(listOf(clip("x", 5, "invoice one")), "c1")
        remote.pages["|c1"] = ClipPage(listOf(clip("z", 3, "invoice two")), null)
        remote.failPages += "|c1"
        val vm = collecting(vm())
        vm.onQueryChange("invoice")
        vm.searchOlder()
        advanceUntilIdle()
        assertEquals(listOf("x"), vm.ids())
        assertTrue("stopped part-way: offer it again", vm.state.value.canSearchOlder)

        vm.searchOlder()
        advanceUntilIdle()
        assertEquals(listOf("x", "z"), vm.ids())
        assertEquals(false, vm.state.value.canSearchOlder)
    }

    @Test
    fun searchOlderThatFindsNothingStopsOffering() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 30, "Hello"))
        remote.pages["|"] = ClipPage(listOf(clip("y", 4, "nope")), null)
        val vm = collecting(vm())
        vm.onQueryChange("zebra")
        vm.searchOlder()
        advanceUntilIdle()
        assertEquals(emptyList<String>(), vm.ids())
        assertEquals("the whole history was searched", false, vm.state.value.canSearchOlder)

        vm.onQueryChange("")
        advanceUntilIdle()
        assertEquals("no query, no button", false, vm.state.value.canSearchOlder)
    }

    private fun mixed() = listOf(
        clip("t", 6, "plain note"),
        clip("l", 5, "https://example.com", ClipKind.LINK),
        clip("i", 4, null, ClipKind.IMAGE),
        fileClip("f").copy(seq = 3),
        clip("u", 2, null, ClipKind.UNKNOWN),
    )

    @Test
    fun filterShowsOneKindAndTappingAgainOrAllReturnsToAll() = runTest(dispatcher) {
        cache.value = mixed()
        val vm = collecting(vm())
        advanceUntilIdle()
        assertEquals(null, vm.state.value.filter)
        assertEquals(listOf("t", "l", "i", "f", "u"), vm.ids())

        vm.onFilter(ClipKind.LINK)
        advanceUntilIdle()
        assertEquals(ClipKind.LINK, vm.state.value.filter)
        assertEquals(listOf("l"), vm.ids())

        vm.onFilter(ClipKind.FILE)   // one at a time
        advanceUntilIdle()
        assertEquals(listOf("f"), vm.ids())

        vm.onFilter(ClipKind.FILE)   // the active chip again: back to All
        advanceUntilIdle()
        assertEquals(null, vm.state.value.filter)
        assertEquals(5, vm.ids().size)

        vm.onFilter(ClipKind.IMAGE)
        vm.onFilter(null)
        advanceUntilIdle()
        assertEquals(5, vm.ids().size)
    }

    @Test
    fun unknownKindShowsOnlyUnderAll() = runTest(dispatcher) {
        cache.value = mixed()
        val vm = collecting(vm())
        for (kind in listOf(ClipKind.TEXT, ClipKind.LINK, ClipKind.IMAGE, ClipKind.FILE)) {
            vm.onFilter(kind)
            advanceUntilIdle()
            assertTrue(kind.name, "u" !in vm.ids())
        }
        vm.onFilter(null)
        advanceUntilIdle()
        assertTrue("u" in vm.ids())
    }

    @Test
    fun filterCombinesWithThePinnedTabAndSearch() = runTest(dispatcher) {
        cache.value = listOf(
            pinned(clip("t1", 9, "report notes"), 3),
            clip("t2", 8, "report draft"),
            pinned(clip("l1", 7, "https://report.example", ClipKind.LINK), 4),
            clip("l2", 6, "https://other.example", ClipKind.LINK),
        )
        val vm = collecting(vm())
        vm.onFilter(ClipKind.LINK)
        advanceUntilIdle()
        assertEquals(listOf("l1", "l2"), vm.ids())

        vm.onTab(HistoryTab.PINNED)
        advanceUntilIdle()
        assertEquals(ClipKind.LINK, vm.state.value.filter)   // the filter survives a tab switch
        assertEquals(listOf("l1"), vm.ids())

        vm.onTab(HistoryTab.HISTORY)
        vm.onQueryChange("report")
        advanceUntilIdle()
        assertEquals("search runs inside the chosen type", listOf("l1"), vm.ids())

        vm.onFilter(ClipKind.TEXT)
        advanceUntilIdle()
        assertEquals(listOf("t1", "t2"), vm.ids())
    }

    @Test
    fun searchOlderKeepsTheFilter() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 30, "Hello"))
        remote.pages["|"] = ClipPage(
            listOf(clip("x", 5, "old invoice text"), clip("y", 4, "https://invoice.example", ClipKind.LINK)),
            null,
        )
        val vm = collecting(vm())
        vm.onFilter(ClipKind.LINK)
        vm.onQueryChange("invoice")
        advanceUntilIdle()
        assertTrue(vm.state.value.canSearchOlder)

        vm.searchOlder()
        advanceUntilIdle()
        assertEquals(listOf("y"), vm.ids())
        assertEquals(false, vm.state.value.canSearchOlder)

        vm.onFilter(null)   // the hits are kept: All shows both
        advanceUntilIdle()
        assertEquals(listOf("x", "y"), vm.ids())
    }

    @Test
    fun resetFilterReturnsToAll() = runTest(dispatcher) {
        cache.value = mixed()
        val vm = collecting(vm())
        vm.onFilter(ClipKind.IMAGE)
        advanceUntilIdle()
        vm.resetFilter()
        advanceUntilIdle()
        assertEquals(null, vm.state.value.filter)
    }

    @Test
    fun searchSkipsClipsWithoutText() = runTest(dispatcher) {
        cache.value = listOf(
            clip("img", 3, text = null, kind = ClipKind.IMAGE),
            clip("u", 2, text = null, kind = ClipKind.UNKNOWN),
            clip("t", 1, "a picture of text"),
        )
        val vm = collecting(vm())
        vm.onQueryChange("pic")
        advanceUntilIdle()
        assertEquals(listOf("t"), vm.ids())
    }

    @Test
    fun searchMatchesEveryFileName() = runTest(dispatcher) {
        val files = clip("f", 3, text = null, kind = ClipKind.FILE).copy(
            files = listOf(
                xyz.ficdev.privatepaste.core.model.ClipFileInfo("report.pdf", 1, "application/pdf"),
                xyz.ficdev.privatepaste.core.model.ClipFileInfo("Holiday.JPG", 2, "image/jpeg"),
            ),
        )
        cache.value = listOf(files, clip("t", 1, "a report"))
        val vm = collecting(vm())
        vm.onQueryChange("holiday")
        advanceUntilIdle()
        assertEquals(listOf("f"), vm.ids())
        vm.onQueryChange("report")
        advanceUntilIdle()
        assertEquals(listOf("f", "t"), vm.ids())
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
    fun scrollingWhileSearchingLoadsNoPages() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 5, "ab"))
        remote.pages["|"] = ClipPage(listOf(clip("x1", 4, "ab 1")), "n1")
        val vm = collecting(vm())
        vm.onQueryChange("ab")
        vm.onNearEnd()
        advanceUntilIdle()
        assertEquals(listOf("a"), vm.ids())
        assertEquals(emptyList<String>(), events)
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
        assertEquals(listOf<UiText>(UiText.Res(R.string.copied)), messages)
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

    private fun fileClip(id: String) = clip(id, 1, text = null, kind = ClipKind.FILE)
        .copy(files = listOf(ClipFileInfo("a.pdf", 10, "application/pdf")))

    @Test
    fun tapOnAFileClipCopiesTheFilesThenRestores() = runTest(dispatcher) {
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        vm.copy(fileClip("f"))
        advanceUntilIdle()
        assertEquals(listOf("copyFiles:f", "restore:f"), events)
        assertEquals(listOf<UiText>(UiText.Res(R.string.copied)), messages)
    }

    @Test
    fun copyingFilesThatCannotBeHadOfflineSaysTheyNeedAConnection() = runTest(dispatcher) {
        fileCopyWorks = false
        sync.value = SyncState.OFFLINE
        val vm = collecting(vm(online = false))
        val messages = messagesOf(vm)
        vm.copy(fileClip("f"))
        advanceUntilIdle()
        assertEquals(listOf("copyFiles:f"), events)
        assertEquals(listOf<UiText>(UiText.Res(R.string.history_files_offline)), messages)
    }

    @Test
    fun copyingFilesThatFailWhileOnlineSaysCopyFailed() = runTest(dispatcher) {
        fileCopyWorks = false
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        vm.copy(fileClip("f"))
        advanceUntilIdle()
        assertEquals(listOf<UiText>(UiText.Res(R.string.copy_failed)), messages)
        assertEquals(listOf("copyFiles:f"), events)
    }

    @Test
    fun saveToastsSavedOrFailed() = runTest(dispatcher) {
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        vm.save(fileClip("f"))
        advanceUntilIdle()
        fileSaveWorks = false
        vm.save(fileClip("f"))
        advanceUntilIdle()
        assertEquals(listOf<UiText>(UiText.Res(R.string.saved_to_downloads), UiText.Res(R.string.save_failed)), messages)
    }

    @Test
    fun shareEmitsTheIntentAndFailureToasts() = runTest(dispatcher) {
        val vm = collecting(vm())
        val shared = mutableListOf<Intent>()
        backgroundScope.launch(UnconfinedTestDispatcher(testScheduler)) { vm.shares.collect { shared += it } }
        val messages = messagesOf(vm)
        vm.share(fileClip("f"))
        advanceUntilIdle()
        assertEquals(1, shared.size)
        assertSame(shareIntent, shared[0])
        assertEquals(emptyList<UiText>(), messages)

        shareIntent = null
        sync.value = SyncState.OFFLINE
        vm.share(fileClip("f"))
        advanceUntilIdle()
        assertEquals(1, shared.size)
        assertEquals(listOf<UiText>(UiText.Res(R.string.history_files_offline)), messages)
    }

    @Test
    fun restoreFailureIsSwallowed() = runTest(dispatcher) {
        remote.failRestore = true
        val vm = collecting(vm())
        val messages = messagesOf(vm)
        vm.copy(clip("a", 1, "x"))
        advanceUntilIdle()
        assertEquals(listOf("copyText:x", "restore:a"), events)
        assertEquals(listOf<UiText>(UiText.Res(R.string.copied)), messages)
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
                UiText.Res(R.string.copied),
                UiText.Res(R.string.copied),
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
        assertEquals(listOf<UiText>(UiText.Res(R.string.copied)), messages)   // the local copy happened
        assertEquals(1, unauthorizedCalls)
    }

    @Test
    fun staleTabResponseIsDropped() = runTest(dispatcher) {
        cache.value = listOf(clip("a", 9, "cached"))
        val gate = CompletableDeferred<Unit>()
        remote.gates["P:|"] = gate
        remote.pages["P:|"] = ClipPage(listOf(pinned(clip("old", 5, "pinned on the server"), 1)), null)
        val vm = collecting(vm())

        vm.onTab(HistoryTab.PINNED)
        runCurrent()
        assertEquals(listOf("clips:P:|"), events)   // in flight, suspended

        vm.onTab(HistoryTab.HISTORY)
        gate.complete(Unit)                          // the Pinned answer arrives after the tab moved on
        advanceUntilIdle()
        assertEquals(listOf("a"), vm.ids())
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
        assertEquals(listOf<UiText>(UiText.Res(R.string.copy_failed)), messages)
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
        /** A response for a "[P:]|cursor" key waits until its gate completes. */
        val gates = HashMap<String, CompletableDeferred<Unit>>()
        val images = HashMap<String, ByteArray>()
        var failRestore = false
        var failDelete = false
        /** Every call throws 401: this device was revoked. */
        var revoked = false

        var failPin = false
        var pinNotFound = false

        override suspend fun pin(id: String, pinned: Boolean): ClipDto {
            events += "pin:$id:$pinned"
            if (revoked) throw ApiException.Unauthorized()
            if (pinNotFound) throw ApiException.Rejected(404, "none")
            if (failPin) throw ApiException.Transient("down")
            val held = cache.value.first { it.id == id }
            return held.copy(seq = held.seq + 100, pinnedAt = if (pinned) base.plusSeconds(3600).toString() else null)
        }

        val limits = mutableListOf<Int>()

        /** Page keys that fail once. */
        val failPages = mutableSetOf<String>()

        override suspend fun clips(cursor: String?, limit: Int, pinned: Boolean): ClipPage {
            val key = (if (pinned) "P:" else "") + "|${cursor.orEmpty()}"
            events += "clips:$key"
            limits += limit
            if (failPages.remove(key)) throw ApiException.Transient("down")
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
