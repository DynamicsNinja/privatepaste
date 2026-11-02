package xyz.ficdev.privatepaste.sync

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import xyz.ficdev.privatepaste.core.model.ServerInfo
import xyz.ficdev.privatepaste.core.model.ServerLimits
import java.io.IOException

class ServerInfoCacheTest {
    private val limits = ServerLimits(textKb = 1024, imageMb = 20, fileMb = 25)
    private val old = ServerInfo(protocol = 2, kinds = listOf("text", "link", "image"), limits = limits)
    private val upgraded = ServerInfo(protocol = 2, kinds = listOf("text", "link", "image", "file"), limits = limits)

    private var answer: () -> ServerInfo = { old }
    private var asks = 0
    private val cache = ServerInfoCache {
        asks++
        answer()
    }

    @Test
    fun asksOnceUntilInvalidated() = runBlocking {
        assertEquals(old, cache.get())
        answer = { upgraded }
        assertEquals(old, cache.get())
        assertEquals(1, asks)

        cache.invalidate()   // the sync link connected again

        assertEquals(upgraded, cache.get())
        assertEquals(upgraded, cache.get())
        assertEquals(2, asks)
    }

    @Test
    fun aFailedAskKeepsTheLastAnswerAndTriesAgainNextTime() = runBlocking {
        cache.get()
        cache.invalidate()
        answer = { throw IOException("offline") }

        assertEquals(old, cache.get())

        answer = { upgraded }
        assertEquals(upgraded, cache.get())
        assertEquals(3, asks)
    }

    @Test
    fun aFailedFirstAskPropagates() {
        answer = { throw IOException("offline") }
        assertThrows(IOException::class.java) { runBlocking { cache.get() } }
    }
}
