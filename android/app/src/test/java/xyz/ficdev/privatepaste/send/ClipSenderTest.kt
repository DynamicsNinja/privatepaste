package xyz.ficdev.privatepaste.send

import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.mockwebserver.MockResponse
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.core.ContentHash
import xyz.ficdev.privatepaste.core.model.ClipKind
import java.io.ByteArrayInputStream
import java.io.InputStream
import java.time.Instant

@RunWith(RobolectricTestRunner::class)
class ClipSenderTest {
    @get:Rule val tmp = TemporaryFolder()
    private lateinit var f: SenderFixture

    @Before fun setUp() { f = SenderFixture(tmp.newFolder()) }
    @After fun tearDown() { f.shutdown() }

    private fun body(text: String) = Json.parseToJsonElement(text).jsonObject

    @Test
    fun sendTextLiveSuccessRecordsCapture() = runBlocking {
        f.respondWith { f.json(f.clipJson("c1", "hello", lastCopiedAt = "2026-10-28T12:00:05+00:00")) }

        val result = f.sender.sendText("hello")

        assertEquals(SendResult.Sent, result)
        assertEquals("Sent to your devices", result.message)
        val req = f.server.takeRequest()
        assertEquals("/api/clips/text", req.path)
        val sent = body(req.body.readUtf8())
        assertEquals("false", sent["backfill"]!!.jsonPrimitive.content)
        assertEquals(Instant.parse("2026-10-28T12:00:05Z"), f.capture.lastLocalCaptureAt)
        assertEquals("c1", f.cache.items.value.single().id)
        assertEquals("a live send is not part of the sync stream", 0L, f.cache.highestSeq)
        assertTrue(f.queue.isEmpty)
    }

    @Test
    fun transientFailureQueuesAndToastsOffline() = runBlocking {
        f.respondWith { f.problem(503, "down") }

        val result = f.sender.sendText("later")

        assertEquals(SendResult.Queued, result)
        assertEquals("Offline. Will send when connected.", result.message)
        val queued = f.queue.peek()!!
        assertEquals("later", queued.text)
        assertEquals(f.now, queued.copiedAt)
        assertEquals(f.now, f.capture.lastLocalCaptureAt)
    }

    @Test
    fun queuedSendsFlushInOrderAsBackfill() = runBlocking {
        val t1 = Instant.parse("2026-10-28T10:00:00Z")
        val t2 = Instant.parse("2026-10-28T10:01:00Z")
        f.queue.enqueueText("first", t1)
        f.queue.enqueueText("second", t2)
        f.respondWith { req ->
            val text = body(req.body.readUtf8())["text"]!!.jsonPrimitive.content
            f.json(f.clipJson("id-$text", text))
        }

        f.sender.flushQueue()

        val a = body(f.server.takeRequest().body.readUtf8())
        val b = body(f.server.takeRequest().body.readUtf8())
        assertEquals("first", a["text"]!!.jsonPrimitive.content)
        assertEquals("second", b["text"]!!.jsonPrimitive.content)
        assertEquals("true", a["backfill"]!!.jsonPrimitive.content)
        assertEquals("true", b["backfill"]!!.jsonPrimitive.content)
        assertEquals(t1.toString(), a["copiedAt"]!!.jsonPrimitive.content)
        assertEquals(t2.toString(), b["copiedAt"]!!.jsonPrimitive.content)
        assertTrue(f.queue.isEmpty)
        assertEquals(2, f.cache.items.value.size)
        assertNull("backfill does not record capture time", f.capture.lastLocalCaptureAt)
        assertTrue(f.notices.isEmpty())
    }

    @Test
    fun poisonItemRotatesThenDrops() = runBlocking {
        f.queue.enqueueText("poison", f.now)
        f.queue.enqueueText("good", f.now)
        f.respondWith { req ->
            val text = body(req.body.readUtf8())["text"]!!.jsonPrimitive.content
            if (text == "poison") f.problem(500, "boom") else f.json(f.clipJson("g", "good"))
        }

        // Attempts 1..4 stop the flush at the poison head.
        repeat(4) { f.sender.flushQueue() }
        assertEquals("poison", f.queue.peek()!!.text)
        assertEquals(4, f.queue.peek()!!.attempts)
        assertEquals(4, f.server.requestCount)

        // Attempt 5 rotates it behind "good", which goes out in the same call.
        f.sender.flushQueue()
        assertEquals("poison", f.queue.peek()!!.text)
        assertEquals(1, f.queue.size)
        assertEquals(5, f.queue.peek()!!.attempts)
        assertTrue(f.notices.isEmpty())

        repeat(4) { f.sender.flushQueue() } // attempts 6..9
        assertEquals(1, f.queue.size)
        f.sender.flushQueue()               // attempt 10
        assertTrue(f.queue.isEmpty)
        assertEquals(listOf("Couldn't sync a clip after several tries."), f.notices)
    }

    @Test
    fun rejectedShowsServerMessage() = runBlocking {
        f.respondWith { f.problem(413, "Too big for the server") }

        val result = f.sender.sendText("x")

        assertEquals(SendResult.Rejected("Too big for the server"), result)
        assertEquals("Too big for the server", result.message)
        assertTrue(f.queue.isEmpty)
    }

    @Test
    fun flushDropsRejectedItemWithItsMessage() = runBlocking {
        f.queue.enqueueText("bad", f.now)
        f.respondWith { f.problem(400, "Nope") }

        f.sender.flushQueue()

        assertTrue(f.queue.isEmpty)
        assertEquals(listOf("Nope"), f.notices)
    }

    @Test
    fun unauthorizedLiveIsSignedOutAndFlushRethrows() = runBlocking {
        f.respondWith { MockResponse().setResponseCode(401) }
        assertEquals(SendResult.SignedOut, f.sender.sendText("x"))
        assertTrue(f.queue.isEmpty)

        f.queue.enqueueText("kept", f.now)
        try {
            f.sender.flushQueue()
            fail("expected Unauthorized")
        } catch (_: ApiException.Unauthorized) {
        }
        assertEquals("kept", f.queue.peek()!!.text)
        assertEquals(0, f.queue.peek()!!.attempts)
    }

    @Test
    fun flushDropsQueuedImageWhoseFileIsGone() = runBlocking {
        f.queue.enqueueImage(ByteArray(10), "image/png", f.now)
        f.queue.enqueueText("after", f.now)
        f.imageDir.listFiles()!!.forEach { it.delete() }
        f.respondWith { f.json(f.clipJson("a", "after")) }

        f.sender.flushQueue()

        assertTrue(f.queue.isEmpty)
        assertEquals(1, f.server.requestCount)
        assertTrue(f.notices.isEmpty())
    }

    @Test
    fun oversizeTextRefused() = runBlocking {
        val result = f.sender.sendText("a".repeat(1024 * 1024 + 1))

        assertEquals(SendResult.TooLarge("Clip too large to sync (text over 1 MB)."), result)
        assertEquals(0, f.server.requestCount)
        assertTrue(f.queue.isEmpty)
    }

    @Test
    fun textAtLimitIsSent() = runBlocking {
        f.respondWith { f.json(f.clipJson("c", "x")) }
        assertEquals(SendResult.Sent, f.sender.sendText("a".repeat(1024 * 1024)))
    }

    @Test
    fun oversizeImageIsRefusedWithoutReadingWholeStream() = runBlocking {
        val limit = 20L * 1024 * 1024
        var read = 0L
        val endless = object : InputStream() {
            override fun read(): Int { read++; return 7 }
            override fun read(b: ByteArray, off: Int, len: Int): Int { read += len; return len }
        }

        val result = f.sender.sendImage({ endless }, "image/jpeg")

        assertEquals(SendResult.TooLarge("Clip too large to sync (image over 20 MB)."), result)
        assertTrue("read $read bytes", read <= limit + 1)
        assertEquals(0, f.server.requestCount)
    }

    @Test
    fun alreadySyncedSkipsUpload() = runBlocking {
        f.cache.markApplied(f.now, ContentHash.text("same"))

        val result = f.sender.sendText("same")

        assertEquals(SendResult.AlreadySynced, result)
        assertEquals("Already on your other devices", result.message)
        assertEquals(0, f.server.requestCount)
    }

    @Test
    fun undecodableImageIsUploadedAsIsAndQueuedOffline() = runBlocking {
        val bytes = ByteArray(300) { it.toByte() }
        f.respondWith { f.problem(502, "gateway") }

        val result = f.sender.sendImage({ ByteArrayInputStream(bytes) }, "image/png")

        assertEquals(SendResult.Queued, result)
        val queued = f.queue.peek()!!
        assertEquals(ClipKind.IMAGE, queued.kind)
        assertEquals("image/png", queued.mime)
        assertTrue(bytes.contentEquals(f.queue.imageBytes(queued)))
        assertEquals(f.now, f.capture.lastLocalCaptureAt)
        assertEquals(1, f.server.requestCount)
    }
}
