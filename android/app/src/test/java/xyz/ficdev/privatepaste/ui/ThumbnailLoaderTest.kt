package xyz.ficdev.privatepaste.ui

import android.graphics.Bitmap
import androidx.compose.ui.graphics.asImageBitmap
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ClipPage
import xyz.ficdev.privatepaste.storage.InMemoryKeyProvider
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import xyz.ficdev.privatepaste.ui.history.ClipRemote
import xyz.ficdev.privatepaste.ui.history.ThumbnailLoader
import java.time.Instant

@RunWith(RobolectricTestRunner::class)
class ThumbnailLoaderTest {

    @get:Rule val tmp = TemporaryFolder()

    private val dispatcher = StandardTestDispatcher()
    private val fetched = mutableListOf<String>()
    private val decoded = mutableListOf<Int>()
    private var unauthorizedCalls = 0

    private val remote = object : ClipRemote {
        override suspend fun clips(q: String?, cursor: String?, limit: Int, pinned: Boolean) = ClipPage(emptyList())
        override suspend fun pin(id: String, pinned: Boolean): ClipDto = throw UnsupportedOperationException()
        override suspend fun restore(id: String): ClipDto? = null
        override suspend fun delete(id: String) = true
        override suspend fun image(id: String) = ByteArray(0)
        override suspend fun thumbnail(id: String): ByteArray {
            fetched += id
            if (id == "missing") throw ApiException.Rejected(404, "none")
            if (id == "revoked") throw ApiException.Unauthorized()
            return byteArrayOf(9, 9)
        }
    }

    private fun image(id: String, hash: String) = ClipDto(
        id = id, seq = 1, kind = ClipKind.IMAGE, hash = hash, width = 4, height = 4, sizeBytes = 100,
        sourceDeviceId = "pc", lastCopiedAt = Instant.parse("2026-10-28T10:00:00Z"),
    )

    private fun loader(store: ThumbnailStore, capacity: Int = 100) = ThumbnailLoader(
        store = store,
        remote = { remote },
        decode = { bytes -> decoded += bytes.size; Bitmap.createBitmap(1, 1, Bitmap.Config.ARGB_8888).asImageBitmap() },
        io = dispatcher,
        capacity = capacity,
        onUnauthorized = { unauthorizedCalls++ },
    )

    @Test
    fun storeFirstThenApiThenStoredAndKeptInMemory() = runTest(dispatcher) {
        val store = ThumbnailStore(tmp.newFolder("thumbs"), InMemoryKeyProvider())
        store.put("hashA", byteArrayOf(1, 2, 3))
        val loader = loader(store)

        val a = loader.load(image("a", "hashA"))
        assertEquals(emptyList<String>(), fetched)   // from the store, no network

        loader.load(image("b", "hashB"))
        assertEquals(listOf("b"), fetched)
        assertArrayEquals(byteArrayOf(9, 9), store.get("hashB"))

        assertSame(a, loader.load(image("a", "hashA")))   // memory hit: no second decode
        assertEquals(listOf(3, 2), decoded)
        assertSame(a, loader.peek("hashA"))
    }

    @Test
    fun failuresGiveNull() = runTest(dispatcher) {
        val store = ThumbnailStore(tmp.newFolder("thumbs"), InMemoryKeyProvider())
        assertNull(loader(store).load(image("missing", "hashM")))
        assertNull(store.get("hashM"))
    }

    @Test
    fun unauthorizedThumbnailRoutesToTheRevokePath() = runTest(dispatcher) {
        val store = ThumbnailStore(tmp.newFolder("thumbs"), InMemoryKeyProvider())
        assertNull(loader(store).load(image("revoked", "hashR")))
        assertEquals(1, unauthorizedCalls)
    }

    @Test
    fun memoryIsAnLru() = runTest(dispatcher) {
        val store = ThumbnailStore(tmp.newFolder("thumbs"), InMemoryKeyProvider())
        val loader = loader(store, capacity = 2)
        loader.load(image("a", "hashA"))
        loader.load(image("b", "hashB"))
        loader.load(image("a", "hashA"))   // touch a
        loader.load(image("c", "hashC"))   // evicts b
        assertNull(loader.peek("hashB"))
        assertEquals(true, loader.peek("hashA") != null && loader.peek("hashC") != null)
    }
}
