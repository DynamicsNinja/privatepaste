package xyz.ficdev.privatepaste.storage

import org.junit.Assert.assertArrayEquals
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
import xyz.ficdev.privatepaste.core.model.ClipKind
import java.io.File
import java.time.Instant

@RunWith(RobolectricTestRunner::class)
class OutboundQueueTest {

    @get:Rule val tmp = TemporaryFolder()

    private val keys = InMemoryKeyProvider()
    private val t0 = Instant.parse("2026-10-28T10:00:00Z")
    private val imageDir get() = File(tmp.root, "queue-images")
    private val png = "PNG-IMAGE-PLAINTEXT-MARKER".toByteArray() + ByteArray(256) { it.toByte() }

    private fun queue() = OutboundQueue(CryptoFile(File(tmp.root, "queue.bin"), keys, KeyAliases.QUEUE), imageDir, keys)

    @Test
    fun orderIsFifo() {
        val q = queue()
        assertTrue(q.isEmpty)
        assertNull(q.peek())
        q.enqueueText("one", t0)
        q.enqueueImage(png, "image/png", t0.plusSeconds(1))
        q.enqueueText("three", t0.plusSeconds(2))
        assertEquals(3, q.size)

        val first = q.peek()!!
        assertEquals(ClipKind.TEXT, first.kind)
        assertEquals("one", first.text)
        assertEquals(t0, first.copiedAt)
        q.removeHead()

        val second = q.peek()!!
        assertEquals(ClipKind.IMAGE, second.kind)
        assertEquals("image/png", second.mime)
        assertArrayEquals(png, q.imageBytes(second))
        q.removeHead()
        assertFalse("image file deleted with its entry", File(imageDir, second.imageFile!!).exists())

        assertEquals("three", q.peek()!!.text)
        q.removeHead()
        assertTrue(q.isEmpty)
        q.removeHead()   // no-op when empty
        assertEquals(0, q.recordFailure())
    }

    @Test
    fun imageBytesAreEncryptedOnDisk() {
        val q = queue()
        q.enqueueImage(png, "image/png", t0)
        val files = imageDir.listFiles()!!.filter { it.isFile }
        assertEquals(1, files.size)
        val onDisk = String(files.single().readBytes(), Charsets.ISO_8859_1)
        assertFalse(onDisk.contains("PNG-IMAGE-PLAINTEXT-MARKER"))
        q.enqueueText("queued text marker", t0)
        assertFalse(String(File(tmp.root, "queue.bin").readBytes(), Charsets.ISO_8859_1).contains("queued text marker"))
    }

    @Test
    fun moveHeadToBackKeepsAttempts() {
        val q = queue()
        q.enqueueText("a", t0)
        q.enqueueText("b", t0)
        assertEquals(1, q.recordFailure())
        assertEquals(2, q.recordFailure())
        q.moveHeadToBack()
        assertEquals("b", q.peek()!!.text)
        assertEquals(0, q.peek()!!.attempts)
        q.removeHead()
        assertEquals("a", q.peek()!!.text)
        assertEquals(2, q.peek()!!.attempts)
        q.moveHeadToBack()   // single item: stays
        assertEquals("a", q.peek()!!.text)
    }

    @Test
    fun queueSurvivesAReload() {
        val q = queue()
        q.enqueueText("a", t0)
        q.enqueueImage(png, "image/png", t0.plusSeconds(5))
        q.recordFailure()

        val reloaded = queue()
        assertEquals(2, reloaded.size)
        val head = reloaded.peek()!!
        assertEquals("a", head.text)
        assertEquals(1, head.attempts)
        assertEquals(t0, head.copiedAt)
        reloaded.removeHead()
        val image = reloaded.peek()!!
        assertNotNull(image.imageFile)
        assertArrayEquals(png, reloaded.imageBytes(image))
        assertEquals(t0.plusSeconds(5), image.copiedAt)
    }

    @Test
    fun corruptQueueStartsEmpty() {
        queue().enqueueImage(png, "image/png", t0)
        File(tmp.root, "queue.bin").writeBytes(ByteArray(80) { 3 })
        val q = queue()
        assertTrue(q.isEmpty)
        assertTrue("orphaned image files are removed", imageDir.listFiles().orEmpty().isEmpty())
    }

    @Test
    fun clearDeletesTheImageFiles() {
        val q = queue()
        q.enqueueImage(png, "image/png", t0)
        q.enqueueImage(png, "image/png", t0)
        q.enqueueText("x", t0)
        assertEquals(2, imageDir.listFiles()!!.size)
        q.clear()
        assertTrue(q.isEmpty)
        assertEquals(0, q.size)
        assertTrue(imageDir.listFiles().orEmpty().isEmpty())
        assertTrue(queue().isEmpty)
    }
}
