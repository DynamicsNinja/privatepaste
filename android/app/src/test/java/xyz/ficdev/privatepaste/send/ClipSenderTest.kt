package xyz.ficdev.privatepaste.send

import android.graphics.BitmapFactory
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import java.io.IOException
import java.util.concurrent.TimeUnit
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
import org.robolectric.annotation.GraphicsMode
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.api.UpdateRequiredException
import xyz.ficdev.privatepaste.core.ContentHash
import xyz.ficdev.privatepaste.core.crypto.ClipCodec
import xyz.ficdev.privatepaste.core.model.ClipKind
import java.io.ByteArrayInputStream
import java.io.InputStream
import java.time.Instant

@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class ClipSenderTest {
    @get:Rule val tmp = TemporaryFolder()
    private lateinit var f: SenderFixture

    @Before fun setUp() { f = SenderFixture(tmp.newFolder()) }
    @After fun tearDown() { f.shutdown() }

    @Test
    fun sendTextLiveSuccessRecordsCapture() = runBlocking {
        f.respondWith { f.json(f.clipJson("c1", "hello", lastCopiedAt = "2026-10-28T12:00:05+00:00")) }

        val result = f.sender.sendText("hello")

        assertEquals(SendResult.Sent, result)
        assertEquals("Sent to your devices", result.message)
        val req = f.server.takeRequest()
        assertEquals("/api/clips", req.path)
        val upload = f.uploaded(req)
        assertEquals("hello", upload.text)
        assertEquals(false, upload.meta.backfill)
        assertNull("a live send takes the server's time", upload.meta.copiedAt)
        assertEquals(f.vaultKey.keyId, upload.meta.keyId)
        assertEquals(ClipCodec(f.vaultKey).keyedHash(ContentHash.text("hello")), upload.meta.hash)
        assertTrue("nothing travels in the clear", !req.body.clone().readUtf8().contains("hello"))
        assertEquals(Instant.parse("2026-10-28T12:00:05Z"), f.capture.lastLocalCaptureAt)
        // The cache holds the opened clip.
        assertEquals("hello", f.cache.items.value.single().text)
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
    fun withoutAVaultKeyASendIsQueued() = runBlocking {
        f.codec = null
        assertEquals(SendResult.Queued, f.sender.sendText("locked"))
        assertEquals(0, f.server.requestCount)
        assertEquals("locked", f.queue.peek()!!.text)

        f.sender.flushQueue()   // still locked: nothing to seal with, so it waits
        assertEquals(0, f.server.requestCount)
        assertEquals(1, f.queue.size)
    }

    @Test
    fun queuedSendsFlushInOrderAsBackfill() = runBlocking {
        val t1 = Instant.parse("2026-10-28T10:00:00Z")
        val t2 = Instant.parse("2026-10-28T10:01:00Z")
        f.queue.enqueueText("first", t1)
        f.queue.enqueueText("second", t2)
        f.respondWith { f.echo(it) }

        f.sender.flushQueue()

        val a = f.uploaded(f.server.takeRequest())
        val b = f.uploaded(f.server.takeRequest())
        assertEquals("first", a.text)
        assertEquals("second", b.text)
        assertTrue(a.meta.backfill)
        assertTrue(b.meta.backfill)
        assertEquals(t1, a.meta.copiedAt)
        assertEquals(t2, b.meta.copiedAt)
        assertTrue(f.queue.isEmpty)
        assertEquals(setOf("first", "second"), f.cache.items.value.map { it.text }.toSet())
        assertNull("backfill does not record capture time", f.capture.lastLocalCaptureAt)
        assertTrue(f.notices.isEmpty())
    }

    @Test
    fun poisonItemRotatesThenDrops() = runBlocking {
        f.queue.enqueueText("poison", f.now)
        f.queue.enqueueText("good", f.now)
        f.respondWith { req ->
            if (f.uploaded(req).text == "poison") f.problem(500, "boom") else f.echo(req, "g")
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
    fun flushDropsAClearedItemWithoutANotification() = runBlocking {
        f.queue.enqueueText("old", f.now)
        f.queue.enqueueText("fresh", f.now)
        var calls = 0
        f.respondWith {
            if (calls++ == 0) f.problem(410, "History was cleared")
            else f.echo(it, "c2")
        }

        f.sender.flushQueue()

        assertTrue(f.queue.isEmpty)
        assertTrue("no notification for a cleared clip", f.notices.isEmpty())
        assertEquals(listOf("c2"), f.cache.items.value.map { it.id })
    }

    @Test
    fun keyResetOnFlushClearsQueueOnce() = runBlocking {
        repeat(3) { f.queue.enqueueText("old key $it", f.now) }
        f.respondWith { f.problem(409, "Encryption was reset.", title = "Encryption was reset") }

        f.sender.flushQueue()

        assertTrue(f.queue.isEmpty)
        assertEquals("no upload under the old key after the first 409", 1, f.server.requestCount)
        assertEquals(listOf(f.vaultKey.keyId), f.resetKeyIds)
        assertTrue("no per-clip notification", f.notices.isEmpty())

        f.sender.flushQueue()   // nothing left to retry
        assertEquals(1, f.server.requestCount)
        assertEquals(1, f.keyResets.get())
    }

    @Test
    fun keyResetOnALiveSendSignalsTheReset() = runBlocking {
        f.respondWith { f.problem(409, "Encryption was reset.", title = "Encryption was reset") }

        val result = f.sender.sendText("x")

        assertEquals(SendResult.Rejected("Encryption was reset."), result)
        assertEquals(1, f.keyResets.get())
        assertTrue("not queued under the old key", f.queue.isEmpty)
    }

    @Test
    fun updateRequiredOnFlushKeepsTheItemAndPropagates() = runBlocking {
        f.queue.enqueueText("kept", f.now)
        f.respondWith { f.problem(426, "Update PrivatePaste to keep syncing.") }
        try {
            f.sender.flushQueue()
            fail("expected UpdateRequiredException")
        } catch (_: UpdateRequiredException) {
        }
        assertEquals("kept", f.queue.peek()!!.text)
        assertEquals(0, f.queue.peek()!!.attempts)
        assertTrue(f.notices.isEmpty())
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
        f.queue.enqueueImage(SenderFixture.png(), "image/png", f.now)
        f.queue.enqueueText("after", f.now)
        f.imageDir.listFiles()!!.forEach { it.delete() }
        f.respondWith { f.echo(it) }

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
    fun multiByteTextOverOneMegabyteIsRefused() = runBlocking {
        val text = "é".repeat(600_000) // 600k chars, 1.2 MB in UTF-8
        assertEquals(SendResult.TooLarge("Clip too large to sync (text over 1 MB)."), f.sender.sendText(text))
        assertEquals(0, f.server.requestCount)
    }

    @Test
    fun textIsCheckedAgainstTheServersLimit() = runBlocking {
        f.limits = f.limits.copy(textKb = 4)
        f.respondWith { f.echo(it) }

        assertEquals(SendResult.TooLarge("Clip too large to sync (text over 4 KB)."), f.sender.sendText("a".repeat(4097)))
        assertEquals(0, f.server.requestCount)
        assertEquals(SendResult.Sent, f.sender.sendText("a".repeat(4096)))
        assertEquals(1, f.server.requestCount)
    }

    @Test
    fun withTheLimitsUnknownTextIsCheckedAgainstOneMegabyte() = runBlocking {
        f.limitsError = ApiException.Transient("offline")
        f.respondWith { f.problem(503, "down") }

        assertEquals(SendResult.TooLarge("Clip too large to sync (text over 1 MB)."), f.sender.sendText("a".repeat(1024 * 1024 + 1)))
        assertEquals(0, f.server.requestCount)
        assertEquals(SendResult.Queued, f.sender.sendText("a".repeat(1024 * 1024)))
    }

    @Test
    fun queuedImageFlushesAsBackfillSealed() = runBlocking {
        val png = SenderFixture.png(30, 12)
        val t = Instant.parse("2026-10-28T09:00:00Z")
        f.queue.enqueueImage(png, "image/png", t)
        f.respondWith { f.echo(it, "img") }

        f.sender.flushQueue()

        val upload = f.uploaded(f.server.takeRequest())
        assertEquals("image", upload.meta.kind)
        assertTrue(upload.meta.backfill)
        assertEquals(t, upload.meta.copiedAt)
        val opened = ClipCodec(f.vaultKey).decodeBlob(upload.parts.getValue("blob0"))
        val bitmap = BitmapFactory.decodeByteArray(opened, 0, opened.size)
        assertEquals(30, bitmap.width)
        assertEquals(12, bitmap.height)
        assertTrue(upload.parts.containsKey("thumb"))
        assertTrue(f.queue.isEmpty)
        assertEquals(30, f.cache.items.value.single().width)
    }

    @Test
    fun flushDropsAnImageOverTheLimitWithItsMessage() = runBlocking {
        f.queue.enqueueImage(SenderFixture.png(), "image/png", f.now)
        f.limits = f.limits.copy(imageMb = 0)
        f.respondWith { f.echo(it) }

        f.sender.flushQueue()

        assertTrue(f.queue.isEmpty)
        assertEquals(0, f.server.requestCount)
        assertEquals(listOf("Images over 0 MB aren't synced."), f.notices)
    }

    @Test
    fun concurrentFlushesUploadEachItemOnce() = runBlocking {
        repeat(3) { f.queue.enqueueText("t$it", f.now) }
        f.respondWith { f.echo(it).setBodyDelay(150, TimeUnit.MILLISECONDS) }

        listOf(
            async(Dispatchers.Default) { f.sender.flushQueue() },
            async(Dispatchers.Default) { f.sender.flushQueue() },
        ).awaitAll()

        assertEquals(3, f.server.requestCount)
        assertTrue(f.queue.isEmpty)
    }

    @Test
    fun cancellationDuringFlushDoesNotCountAnAttempt() = runBlocking {
        f.queue.enqueueText("slow", f.now)
        f.respondWith { f.json(f.clipJson("x", "x")).setHeadersDelay(10, TimeUnit.SECONDS) }

        val job = launch(Dispatchers.Default) { f.sender.flushQueue() }
        assertTrue(f.server.takeRequest(5, TimeUnit.SECONDS) != null)
        job.cancelAndJoin()

        assertEquals(0, f.queue.peek()!!.attempts)
        assertEquals(1, f.queue.size)
        assertTrue(f.notices.isEmpty())
    }

    @Test
    fun readImageReturnsBytesTooLargeOrUnreadable() = runBlocking {
        val bytes = ByteArray(40) { it.toByte() }
        val ok = f.sender.readImage { ByteArrayInputStream(bytes) }
        assertTrue(bytes.contentEquals((ok as ImageRead.Bytes).bytes))
        val big = object : InputStream() {
            override fun read(): Int = 1
            override fun read(b: ByteArray, off: Int, len: Int): Int = len
        }
        assertEquals(ImageRead.TooLarge, f.sender.readImage { big })
        assertEquals(ImageRead.Unreadable, f.sender.readImage { throw IOException("gone") })
    }

    @Test
    fun sendImageBytesUploadsASealedPng() = runBlocking {
        f.respondWith { f.echo(it, "i") }
        assertEquals(SendResult.Sent, f.sender.sendImageBytes(SenderFixture.png(), "image/png"))
        val upload = f.uploaded(f.server.takeRequest())
        assertEquals("image", upload.meta.kind)
        assertEquals(false, upload.meta.backfill)
        assertEquals(1, upload.parts.keys.count { it.startsWith("blob") })
        assertEquals(20, f.cache.items.value.single().width)
    }

    @Test
    fun tooManyPixelsIsTooLarge() = runBlocking {
        // 7200x7200 declared in the header: refused before decoding or uploading.
        val header = ClipSenderTestImages.pngHeader(7200, 7200)
        assertEquals(SendResult.TooLarge("Images over 50 megapixels aren't synced."), f.sender.sendImageBytes(header, "image/png"))
        assertEquals(0, f.server.requestCount)
        assertTrue(f.queue.isEmpty)
    }

    @Test
    fun onlyForeignContentUrisAreSendable() {
        fun u(s: String) = android.net.Uri.parse(s)
        assertTrue(u("content://media/external/images/1").isSendable())
        assertTrue(u("content://10@media/external/images/1").isSendable())
        assertTrue(!u("content://xyz.ficdev.privatepaste.files/clips/a.png").isSendable())
        assertTrue(!u("content://XYZ.ficdev.privatepaste.files/clips/a.png").isSendable())
        assertTrue(!u("content://5@xyz.ficdev.privatepaste.files/a").isSendable())
        assertTrue(!u("file:///data/data/xyz.ficdev.privatepaste/files/session.bin").isSendable())
        assertTrue(!u("http://example.com/a.png").isSendable())
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
        // Synced clips carry the keyed hash, so that is what a send compares with.
        f.cache.markApplied(f.now, ClipCodec(f.vaultKey).keyedHash(ContentHash.text("same")))

        val result = f.sender.sendText("same")

        assertEquals(SendResult.AlreadySynced, result)
        assertEquals("Already on your other devices", result.message)
        assertEquals(0, f.server.requestCount)
    }

    @Test
    fun undecodableImageIsRefused() = runBlocking {
        val bytes = ByteArray(300) { it.toByte() }

        val result = f.sender.sendImage({ ByteArrayInputStream(bytes) }, "image/png")

        assertEquals(SendResult.Rejected("Couldn't read the image"), result)
        assertEquals(0, f.server.requestCount)
        assertTrue(f.queue.isEmpty)
    }

    @Test
    fun imageSentOfflineIsQueued() = runBlocking {
        val png = SenderFixture.png()
        f.respondWith { f.problem(502, "gateway") }

        val result = f.sender.sendImage({ ByteArrayInputStream(png) }, "image/png")

        assertEquals(SendResult.Queued, result)
        val queued = f.queue.peek()!!
        assertEquals(ClipKind.IMAGE, queued.kind)
        assertEquals("image/png", queued.mime)
        assertTrue(png.contentEquals(f.queue.imageBytes(queued)))
        assertEquals(f.now, f.capture.lastLocalCaptureAt)
        assertEquals(1, f.server.requestCount)
    }

    /** A send in flight when the session ends: its completion writes nothing (the next session's stores are clean). */
    private fun sendThatEndsMidFlight(response: MockResponse): SendResult = runBlocking {
        var ending = false
        val sender = f.sender { ending }
        val release = java.util.concurrent.CountDownLatch(1)
        f.respondWith {
            release.await(10, TimeUnit.SECONDS)
            response
        }
        val send = async(Dispatchers.IO) { sender.sendText("late") }
        f.server.takeRequest(10, TimeUnit.SECONDS) ?: fail("the upload never started")
        ending = true   // sign-out or revoke begins while the upload runs
        release.countDown()
        send.await()
    }

    @Test
    fun sendCompletingAfterSignOutWritesNothing() {
        val result = sendThatEndsMidFlight(f.json(f.clipJson("c1", "late")))

        assertEquals(SendResult.SignedOut, result)
        assertTrue(f.cache.items.value.isEmpty())
        assertNull(f.capture.lastLocalCaptureAt)
        val reloaded = xyz.ficdev.privatepaste.storage.HistoryCache(
            xyz.ficdev.privatepaste.storage.CryptoFile(java.io.File(f.dir, "cache.bin"), f.keys, xyz.ficdev.privatepaste.storage.KeyAliases.CACHE),
        )
        assertTrue("nothing saved", reloaded.items.value.isEmpty())
    }

    @Test
    fun sendFailingAfterSignOutIsNotQueued() {
        val result = sendThatEndsMidFlight(f.problem(503, "down"))

        assertEquals(SendResult.SignedOut, result)
        assertTrue("never reaches the next session's queue", f.queue.isEmpty)
        assertNull(f.capture.lastLocalCaptureAt)
    }

    @Test
    fun aKeyClosedMidSendNeverSealsAndQueuesTheClip() = runBlocking {
        val closed = xyz.ficdev.privatepaste.core.crypto.VaultKey.create().also { it.close() }
        f.codec = ClipCodec(closed)
        f.respondWith { f.echo(it) }

        assertEquals(SendResult.Queued, f.sender.sendText("typed during a reset"))
        assertEquals(SendResult.Queued, f.sender.sendImageBytes(SenderFixture.png(), "image/png"))
        assertEquals("nothing goes out under a wiped key", 0, f.server.requestCount)
        assertEquals(2, f.queue.size)

        f.sender.flushQueue()   // still the closed key: the items wait, no attempt counted
        assertEquals(0, f.server.requestCount)
        assertEquals(0, f.queue.peek()!!.attempts)

        // Once signing out, the same race drops the clip instead.
        assertEquals(SendResult.SignedOut, f.sender { true }.sendText("typed during sign-out"))
        assertEquals(2, f.queue.size)
    }

    private fun shared(name: String, bytes: ByteArray, mime: String? = null) = SharedFile(name, mime) { ByteArrayInputStream(bytes) }

    @Test
    fun readFilesCopiesAndHashesWhileReading() = runBlocking {
        val read = f.sender.readFiles(listOf(shared("a.txt", "hello".toByteArray()), shared("", ByteArray(0), "application/x-thing")))
        read as FilesRead.Ready
        assertEquals(listOf("a.txt", "file"), read.files.map { it.name })
        assertEquals(listOf("text/plain", "application/x-thing"), read.files.map { it.mime })
        assertEquals("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", read.files[0].sha256Hex)
        assertEquals(listOf(5L, 0L), read.files.map { it.size })
        assertTrue(read.files.all { it.file.isFile && it.file.canonicalPath.startsWith(f.outgoing.canonicalPath) })

        f.respondWith { f.echo(it, "f") }
        assertEquals(SendResult.Sent, f.sender.sendFiles(read))
        val upload = f.uploaded(f.server.takeRequest())
        val digests = read.files.map { ContentHash.FileDigest(it.name, it.size, it.sha256Hex) }
        assertEquals(ClipCodec(f.vaultKey).keyedHash(ContentHash.files(digests)), upload.meta.hash)
        assertTrue(f.outgoing.walkTopDown().none { it.isFile })
        assertEquals(ClipKind.FILE, f.cache.items.value.single().kind)
    }

    @Test
    fun aHugeShareStopsReadingAtTheLimit() = runBlocking {
        f.limits = f.limits.copy(fileMb = 1)
        var served = 0L
        val endless = object : InputStream() {
            override fun read(): Int = 0.also { served++ }
            override fun read(b: ByteArray, off: Int, len: Int): Int = len.also { served += it }
        }
        val read = f.sender.readFiles(listOf(SharedFile("big", null) { endless }))
        assertEquals(SendResult.TooLarge("Files over 1 MB aren't synced."), (read as FilesRead.Failed).result)
        assertTrue("stopped soon after the limit", served < 2L * 1024 * 1024)
        assertTrue(f.outgoing.walkTopDown().none { it.isFile })
    }

    @Test
    fun atMost100FilesAtOnce() = runBlocking {
        val read = f.sender.readFiles((1..101).map { shared("f$it", byteArrayOf(1)) })
        assertEquals(SendResult.Rejected("Too many files. Share at most 100 at once."), (read as FilesRead.Failed).result)
        assertEquals(0, f.server.requestCount)
    }

    @Test
    fun anUnreadableStreamFailsTheWholeShare() = runBlocking {
        val read = f.sender.readFiles(listOf(shared("a", byteArrayOf(1)), SharedFile("b", null) { throw SecurityException("no grant") }))
        assertEquals(SendResult.Rejected("Couldn't read the files"), (read as FilesRead.Failed).result)
        assertTrue(f.outgoing.walkTopDown().none { it.isFile })
    }

    @Test
    fun lockedOrSignedOutFilesAreNotSent() = runBlocking {
        val read = f.sender.readFiles(listOf(shared("a", byteArrayOf(1)))) as FilesRead.Ready
        f.codec = null
        assertEquals(SendResult.Rejected("Open PrivatePaste to unlock, then share the files again."), f.sender.sendFiles(read))
        assertTrue(f.queue.isEmpty)
        assertTrue(f.outgoing.walkTopDown().none { it.isFile })

        f.limitsError = ApiException.Unauthorized()
        assertEquals(SendResult.SignedOut, (f.sender.readFiles(listOf(shared("a", byteArrayOf(1)))) as FilesRead.Failed).result)
        assertEquals(0, f.server.requestCount)
    }
}

/** A PNG that only declares its size (IHDR), with no real pixel data. */
internal object ClipSenderTestImages {
    fun pngHeader(width: Int, height: Int): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        out.write(byteArrayOf(0x89.toByte(), 'P'.code.toByte(), 'N'.code.toByte(), 'G'.code.toByte(), 13, 10, 26, 10))
        fun chunk(type: String, data: ByteArray) {
            out.write(java.nio.ByteBuffer.allocate(4).putInt(data.size).array())
            val typed = type.toByteArray() + data
            out.write(typed)
            out.write(java.nio.ByteBuffer.allocate(4).putInt(java.util.zip.CRC32().apply { update(typed) }.value.toInt()).array())
        }
        chunk("IHDR", java.nio.ByteBuffer.allocate(13).putInt(width).putInt(height).put(8).put(6).put(0).put(0).put(0).array())
        chunk("IDAT", byteArrayOf(0x78, 0x9c.toByte(), 0x03, 0x00, 0x00, 0x00, 0x00, 0x01))
        chunk("IEND", ByteArray(0))
        return out.toByteArray()
    }
}
