package xyz.ficdev.privatepaste.storage

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import java.io.File
import java.time.Instant
import java.util.Collections
import java.util.concurrent.CountDownLatch
import kotlin.concurrent.thread

@RunWith(RobolectricTestRunner::class)
class HistoryCacheTest {

    @get:Rule val tmp = TemporaryFolder()

    private val keys = InMemoryKeyProvider()
    private val base = Instant.parse("2026-10-28T10:00:00Z")

    private fun cryptoFile() = CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE)
    private fun cache(max: Int = 200) = HistoryCache(cryptoFile(), max)

    private fun clip(id: String, seq: Long, minute: Long = seq, hash: String = "h-$id") = ClipDto(
        id = id, seq = seq, kind = ClipKind.TEXT, text = "text $id", hash = hash, sizeBytes = 6,
        sourceDeviceId = "d1", lastCopiedAt = base.plusSeconds(minute * 60),
    )

    @Test
    fun corruptCacheStartsEmpty() {
        File(tmp.root, "cache.bin").writeBytes(ByteArray(100) { (it * 7).toByte() })
        val c = cache()
        assertTrue(c.items.value.isEmpty())
        assertEquals(0L, c.highestSeq)
        assertNull(c.lastAppliedAt)

        // Decrypts but is not a snapshot.
        cryptoFile().write("[1,2,3]".toByteArray())
        assertTrue(cache().items.value.isEmpty())

        // Key gone.
        cache().apply { upsert(clip("a", 1), fromSync = true); save() }
        keys.forget(KeyAliases.CACHE)
        val afterKeyLoss = cache()
        assertTrue(afterKeyLoss.items.value.isEmpty())
        // Still usable afterwards.
        assertTrue(afterKeyLoss.upsert(clip("b", 2), fromSync = true))
        afterKeyLoss.save()
        assertEquals(listOf("b"), cache().items.value.map { it.id })
    }

    @Test
    fun keepsSourceDevicePlatformAndLoadsCachesWithoutIt() {
        cache().apply {
            upsert(clip("a", 1).copy(sourceDevicePlatform = "android"), fromSync = true)
            upsert(clip("b", 2), fromSync = true)
            save()
        }
        assertEquals(
            mapOf("a" to "android", "b" to null),
            cache().items.value.associate { it.id to it.sourceDevicePlatform },
        )

        // A snapshot as older versions wrote it, without the field.
        cryptoFile().write(
            """{"highestSeq":3,"items":[{"id":"old","seq":3,"kind":"text","text":"x","hash":"h","sizeBytes":1,
               "sourceDeviceId":"d1","sourceDeviceName":"PC","lastCopiedAt":"2026-10-28T10:00:00Z"}]}""".toByteArray(),
        )
        val old = cache().items.value.single()
        assertEquals("old", old.id)
        assertNull(old.sourceDevicePlatform)
    }

    @Test
    fun itemsAreNewestFirst() {
        val c = cache()
        c.upsert(clip("a", 1, minute = 5), fromSync = true)
        c.upsert(clip("b", 2, minute = 9), fromSync = true)
        c.upsert(clip("c", 3, minute = 1), fromSync = true)
        assertEquals(listOf("b", "a", "c"), c.items.value.map { it.id })
    }

    @Test
    fun olderSeqUpsertIsIgnored() {
        val c = cache()
        assertTrue(c.upsert(clip("a", 5), fromSync = true))
        assertFalse(c.upsert(clip("a", 4), fromSync = true))
        assertFalse(c.upsert(clip("a", 5), fromSync = true))
        assertEquals(5L, c.items.value.single().seq)
        assertTrue(c.upsert(clip("a", 6), fromSync = true))
        assertEquals(6L, c.items.value.single().seq)
    }

    @Test
    fun tombstonedUpsertIsIgnored() {
        val c = cache()
        c.upsert(clip("a", 1), fromSync = true)
        assertTrue(c.remove("a", 3))
        assertTrue(c.items.value.isEmpty())
        assertFalse(c.upsert(clip("a", 2), fromSync = true))
        assertFalse(c.upsert(clip("a", 3), fromSync = true))
        assertFalse("removing an unknown id reports nothing removed", c.remove("zzz", 4))
        assertFalse(c.upsert(clip("zzz", 4), fromSync = true))
        assertTrue(c.items.value.isEmpty())
    }

    @Test
    fun ownUploadsDoNotMoveHighestSeq() {
        val c = cache()
        c.upsert(clip("a", 10), fromSync = true)
        assertEquals(10L, c.highestSeq)
        assertTrue(c.upsert(clip("mine", 20), fromSync = false))
        assertEquals(10L, c.highestSeq)
        assertEquals(2, c.items.value.size)

        c.advanceSeq(15)
        assertEquals(15L, c.highestSeq)
        c.advanceSeq(12)
        assertEquals(15L, c.highestSeq)
    }

    @Test
    fun the201stItemTrimsTheOldest() {
        val c = cache()
        for (i in 1..200L) c.upsert(clip("c$i", i), fromSync = true)
        assertEquals(200, c.items.value.size)
        c.upsert(clip("c201", 201), fromSync = true)
        assertEquals(200, c.items.value.size)
        assertFalse(c.items.value.any { it.id == "c1" })
        assertEquals("c201", c.items.value.first().id)
        assertEquals("c2", c.items.value.last().id)
    }

    @Test
    fun clipOlderThanAFullCacheIsNotAdded() {
        val c = cache()
        for (i in 1..200L) c.upsert(clip("c$i", i, minute = 100 + i), fromSync = true)
        val before = c.items.value
        assertFalse(c.upsert(clip("old", 500, minute = 1), fromSync = true))
        assertTrue(c.items.value === before)   // nothing published
        assertFalse(c.items.value.any { it.id == "old" })
        assertEquals(500L, c.highestSeq)   // the sync stream was still read that far
    }

    @Test
    fun markAppliedKeepsTheMaximumAndItsHash() {
        val c = cache()
        c.markApplied(base.plusSeconds(60), "h1")
        c.markApplied(base, "h0")
        assertEquals(base.plusSeconds(60), c.lastAppliedAt)
        assertEquals("h1", c.lastAppliedHash)
        c.markApplied(base.plusSeconds(120), "h2")
        assertEquals(base.plusSeconds(120), c.lastAppliedAt)
        assertEquals("h2", c.lastAppliedHash)
    }

    @Test
    fun sameHashBumpKeepsTheThumbnail() {
        val c = cache()
        val thumbs = ThumbnailStore(File(tmp.root, "thumbs"), keys)
        val png = byteArrayOf(1, 2, 3)
        c.upsert(clip("img", 1, hash = "abc"), fromSync = true)
        thumbs.put("abc", png)

        assertTrue(c.upsert(clip("img", 2, minute = 30, hash = "abc"), fromSync = true))   // re-copied: same content
        thumbs.prune(c.items.value.map { it.hash }.toSet())
        assertNotNull(thumbs.get("abc"))
    }

    @Test
    fun saveThenReloadRestoresState() {
        val c = cache()
        c.upsert(clip("a", 1), fromSync = true)
        c.upsert(clip("b", 7), fromSync = true)
        c.upsert(clip("mine", 9), fromSync = false)
        c.remove("dead", 5)
        c.markApplied(base.plusSeconds(600), "hash-b")
        c.save()

        val reloaded = cache()
        assertEquals(c.items.value, reloaded.items.value)
        assertEquals(7L, reloaded.highestSeq)
        assertEquals(base.plusSeconds(600), reloaded.lastAppliedAt)
        assertEquals("hash-b", reloaded.lastAppliedHash)
        assertFalse("tombstone survived the reload", reloaded.upsert(clip("dead", 4), fromSync = true))
        assertTrue(reloaded.upsert(clip("dead", 6), fromSync = true))
    }

    @Test
    fun tombstonesKeepTheLast1000() {
        val c = cache()
        for (i in 1..1001L) c.remove("t$i", i)
        c.save()
        val reloaded = cache()
        assertTrue("oldest tombstone dropped", reloaded.upsert(clip("t1", 1), fromSync = true))
        assertFalse(reloaded.upsert(clip("t2", 2), fromSync = true))
        assertFalse(reloaded.upsert(clip("t1001", 1001), fromSync = true))
    }

    @Test
    fun clearEmptiesAndDeletesTheFile() {
        val c = cache()
        c.upsert(clip("a", 3), fromSync = true)
        c.markApplied(base, "h")
        c.save()
        c.clear()
        assertTrue(c.items.value.isEmpty())
        assertEquals(0L, c.highestSeq)
        assertNull(c.lastAppliedAt)
        assertNull(c.lastAppliedHash)
        assertFalse(File(tmp.root, "cache.bin").exists())
        assertTrue(cache().items.value.isEmpty())
    }

    @Test
    fun concurrentSavesNeverThrow() {
        val c = cache()
        val errors = Collections.synchronizedList(mutableListOf<Throwable>())
        val start = CountDownLatch(1)
        val savesPerThread = listOf(13, 13, 12, 12)   // 50 saves in all
        val threads = savesPerThread.mapIndexed { t, saves ->
            thread {
                start.await()
                try {
                    repeat(saves) { i ->
                        c.upsert(clip("t$t-$i", (t * 100 + i + 1).toLong()), fromSync = true)
                        c.save()
                    }
                } catch (e: Throwable) {
                    errors += e
                }
            }
        }
        start.countDown()
        threads.forEach { it.join() }

        assertEquals(emptyList<Throwable>(), errors)
        val reloaded = cache()
        assertEquals(50, reloaded.items.value.size)
        assertEquals(c.items.value, reloaded.items.value)
        assertFalse(File(tmp.root, "cache.bin.tmp").exists())
    }
}
