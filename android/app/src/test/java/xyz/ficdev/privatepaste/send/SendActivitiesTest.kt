package xyz.ficdev.privatepaste.send

import android.app.Application
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Looper
import androidx.test.core.app.ApplicationProvider
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import org.robolectric.shadows.ShadowToast
import xyz.ficdev.privatepaste.core.model.ServerLimits
import java.util.concurrent.TimeUnit

/** The send runs in the app scope after the activity has finished, so wait for its toast. */
private fun awaitToast(): String {
    val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10)
    while (ShadowToast.getTextOfLatestToast() == null && System.nanoTime() < deadline) {
        shadowOf(Looper.getMainLooper()).idle()
        Thread.sleep(10)
    }
    shadowOf(Looper.getMainLooper()).idle()
    return ShadowToast.getTextOfLatestToast() ?: throw AssertionError("no toast")
}

private fun awaitFinish(isFinishing: () -> Boolean) {
    val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10)
    while (!isFinishing() && System.nanoTime() < deadline) {
        shadowOf(Looper.getMainLooper()).idle()
        Thread.sleep(10)
    }
    shadowOf(Looper.getMainLooper()).idle()
    assertTrue("activity did not finish", isFinishing())
}

@RunWith(RobolectricTestRunner::class)
@Config(application = TestSenderApp::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class ShareActivityTest {
    @get:Rule val tmp = TemporaryFolder()
    private lateinit var f: SenderFixture

    @Before fun setUp() {
        f = SenderFixture(tmp.newFolder())
        TestSenderApp.sender = f.sender
    }

    @After fun tearDown() {
        TestSenderApp.sender = null
        f.shutdown()
    }

    @Test
    fun shareActivitySendsTextAndFinishes() {
        f.respondWith { f.json(f.clipJson("c1", "shared")) }
        val intent = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, "shared")

        val controller = Robolectric.buildActivity(ShareActivity::class.java, intent).setup()
        awaitFinish { controller.get().isFinishing }

        assertEquals("Sent to your devices", awaitToast())
        assertEquals(1, f.server.requestCount)
        assertEquals("shared", f.cache.items.value.single().text)
    }

    @Test
    fun shareActivitySendsStyledTextAsPlainText() {
        f.respondWith { f.json(f.clipJson("c1", "bold words")) }
        val styled = android.text.SpannableString("bold words").apply {
            setSpan(android.text.style.StyleSpan(android.graphics.Typeface.BOLD), 0, 4, 0)
        }
        val intent = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, styled)

        val controller = Robolectric.buildActivity(ShareActivity::class.java, intent).setup()
        awaitFinish { controller.get().isFinishing }

        assertEquals("Sent to your devices", awaitToast())
        assertEquals(1, f.server.requestCount)
        assertEquals("bold words", f.uploaded(f.server.takeRequest()).text)
    }

    @Test
    fun shareActivityImagePathSendsBytesFromContentUri() {
        f.respondWith { f.echo(it, "i") }
        val uri = android.net.Uri.parse("content://media/external/images/media/7")
        val bytes = SenderFixture.png(12, 8)
        val app = ApplicationProvider.getApplicationContext<Application>()
        shadowOf(app.contentResolver).registerInputStream(uri, java.io.ByteArrayInputStream(bytes))
        val intent = Intent(Intent.ACTION_SEND).setType("image/png").putExtra(Intent.EXTRA_STREAM, uri)

        val controller = Robolectric.buildActivity(ShareActivity::class.java, intent).setup()
        awaitFinish { controller.get().isFinishing }

        assertEquals("Sent to your devices", awaitToast())
        val upload = f.uploaded(f.server.takeRequest())
        assertEquals("image", upload.meta.kind)
        assertEquals(12, f.cache.items.value.single().width)
    }

    @Test
    fun shareActivityRefusesOwnFileProviderAndFileUris() {
        for (s in listOf("content://xyz.ficdev.privatepaste.files/clips/a.png", "file:///data/data/x/session.bin")) {
            ShadowToast.reset()
            val intent = Intent(Intent.ACTION_SEND).setType("image/png").putExtra(Intent.EXTRA_STREAM, android.net.Uri.parse(s))
            val controller = Robolectric.buildActivity(ShareActivity::class.java, intent).setup()
            awaitFinish { controller.get().isFinishing }
            assertEquals("Couldn't read the image", awaitToast())
        }
        assertEquals(0, f.server.requestCount)
    }

    private val app get() = ApplicationProvider.getApplicationContext<Application>()

    private fun stream(uri: String, bytes: ByteArray): android.net.Uri =
        android.net.Uri.parse(uri).also { shadowOf(app.contentResolver).registerInputStream(it, java.io.ByteArrayInputStream(bytes)) }

    private fun share(intent: Intent): String {
        val controller = Robolectric.buildActivity(ShareActivity::class.java, intent).setup()
        awaitFinish { controller.get().isFinishing }
        return awaitToast()
    }

    private fun shareMultiple(vararg uris: android.net.Uri) =
        Intent(Intent.ACTION_SEND_MULTIPLE).setType("*/*").putParcelableArrayListExtra(Intent.EXTRA_STREAM, arrayListOf(*uris))

    @Test
    fun aSharedPdfBecomesOneFileClip() {
        f.respondWith { f.echo(it, "f") }
        val pdf = "%PDF-1.7 report".toByteArray()
        val uri = stream("content://com.example.docs/document/report.pdf", pdf)

        assertEquals("Sent to your devices", share(Intent(Intent.ACTION_SEND).setType("application/pdf").putExtra(Intent.EXTRA_STREAM, uri)))

        val upload = f.uploaded(f.server.takeRequest())
        assertEquals("file", upload.meta.kind)
        assertEquals("report.pdf", upload.files!!.single().name)
        assertEquals(pdf.size.toLong(), upload.files!!.single().size)
        assertEquals("application/pdf", upload.files!!.single().mime)
        assertEquals(pdf.size.toLong(), upload.meta.sizeBytes)
        assertArrayEquals(pdf, xyz.ficdev.privatepaste.core.crypto.ClipCodec(f.vaultKey).decodeBlob(upload.blobs.single()))
        assertEquals(listOf("report.pdf"), f.cache.items.value.single().files!!.map { it.name })
        assertTrue("outgoing copies are removed after the send", f.outgoing.walkTopDown().none { it.isFile })
    }

    @Test
    fun sendMultipleWithAPngAndAPdfBecomesOneClipWithTwoBlobs() {
        f.respondWith { f.echo(it, "f") }
        val png = SenderFixture.png(4, 4)
        val a = stream("content://media/external/images/media/9", png)
        val b = stream("content://com.example.docs/document/notes.pdf", byteArrayOf(1, 2, 3))

        assertEquals("Sent to your devices", share(shareMultiple(a, b)))

        assertEquals(1, f.server.requestCount)
        val upload = f.uploaded(f.server.takeRequest())
        assertEquals("file", upload.meta.kind)
        assertEquals(2, upload.blobs.size)
        assertEquals(listOf("9", "notes.pdf"), upload.files!!.map { it.name })
        assertEquals(listOf(png.size.toLong(), 3L), upload.files!!.map { it.size })
    }

    @Test
    fun aSinglePngStillSendsAnImage() {
        f.respondWith { f.echo(it, "i") }
        val uri = stream("content://media/external/images/media/8", SenderFixture.png(6, 6))

        assertEquals("Sent to your devices", share(Intent(Intent.ACTION_SEND).setType("image/png").putExtra(Intent.EXTRA_STREAM, uri)))

        assertEquals("image", f.uploaded(f.server.takeRequest()).meta.kind)
    }

    @Test
    fun fileSharesRefuseOwnFileProviderAndFileUris() {
        val own = android.net.Uri.parse("content://xyz.ficdev.privatepaste.files/files/c1/a.pdf")
        val path = android.net.Uri.parse("file:///data/data/xyz.ficdev.privatepaste/files/session.bin")
        for (intent in listOf(
            Intent(Intent.ACTION_SEND).setType("application/pdf").putExtra(Intent.EXTRA_STREAM, own),
            Intent(Intent.ACTION_SEND).setType("application/octet-stream").putExtra(Intent.EXTRA_STREAM, path),
            shareMultiple(stream("content://com.example.docs/document/ok.pdf", byteArrayOf(1)), own),
        )) {
            ShadowToast.reset()
            assertEquals("Couldn't read the files", share(intent))
        }
        assertEquals(0, f.server.requestCount)
    }

    @Test
    fun filesOverTheLimitAreNotSent() {
        f.limits = ServerLimits(textKb = 1024, imageMb = 20, fileMb = 1)
        val a = stream("content://com.example.docs/document/a.bin", ByteArray(700 * 1024))
        val b = stream("content://com.example.docs/document/b.bin", ByteArray(400 * 1024))

        assertEquals("Files over 1 MB aren't synced.", share(shareMultiple(a, b)))

        assertEquals(0, f.server.requestCount)
        assertTrue(f.outgoing.walkTopDown().none { it.isFile })
    }

    @Test
    fun anOldServerGetsNoFiles() {
        f.kinds = listOf("text", "link", "image")
        val uri = stream("content://com.example.docs/document/report.pdf", byteArrayOf(1))

        assertEquals(
            "Update your PrivatePaste server to sync files.",
            share(Intent(Intent.ACTION_SEND).setType("application/pdf").putExtra(Intent.EXTRA_STREAM, uri)),
        )
        assertEquals(0, f.server.requestCount)
    }

    @Test
    fun offlineFilesAreNeverQueued() {
        f.respondWith { f.problem(503, "down") }
        val uri = stream("content://com.example.docs/document/report.pdf", byteArrayOf(1, 2))

        assertEquals(
            "Couldn't send the files. Share them again when you're online.",
            share(Intent(Intent.ACTION_SEND).setType("application/pdf").putExtra(Intent.EXTRA_STREAM, uri)),
        )
        assertTrue("nothing is queued", f.queue.isEmpty)
        assertTrue(f.outgoing.walkTopDown().none { it.isFile })

        // Unreachable before the upload (the server's limits can't be read): the same message, nothing read or queued.
        ShadowToast.reset()
        f.limitsError = xyz.ficdev.privatepaste.api.ApiException.Transient("offline")
        val again = stream("content://com.example.docs/document/report2.pdf", byteArrayOf(3))
        assertEquals(
            "Couldn't send the files. Share them again when you're online.",
            share(Intent(Intent.ACTION_SEND).setType("application/pdf").putExtra(Intent.EXTRA_STREAM, again)),
        )
        assertTrue(f.queue.isEmpty)
    }

    @Test
    fun signedOutShareToastsAndFinishes() {
        TestSenderApp.sender = null
        val intent = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, "x")

        val controller = Robolectric.buildActivity(ShareActivity::class.java, intent).setup()

        assertTrue(controller.get().isFinishing)
        assertEquals("Sign in to PrivatePaste first", ShadowToast.getTextOfLatestToast())
        assertEquals(0, f.server.requestCount)
    }
}

@RunWith(RobolectricTestRunner::class)
@Config(application = TestSenderApp::class)
class ClipboardSendActivityTest {
    @get:Rule val tmp = TemporaryFolder()
    private lateinit var f: SenderFixture

    @Before fun setUp() {
        f = SenderFixture(tmp.newFolder())
        TestSenderApp.sender = f.sender
    }

    @After fun tearDown() {
        TestSenderApp.sender = null
        f.shutdown()
    }

    private fun setClipboard(text: String?) {
        val cm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        if (text == null) cm.clearPrimaryClip() else cm.setPrimaryClip(ClipData.newPlainText("t", text))
    }

    @Test
    fun clipboardSendActivityReadsOnFocus() {
        f.respondWith { f.json(f.clipJson("c1", "from clipboard")) }
        setClipboard("from clipboard")

        val activity = Robolectric.buildActivity(ClipboardSendActivity::class.java).setup().get()
        shadowOf(Looper.getMainLooper()).idle()
        assertEquals("nothing is sent before the window has focus", 0, f.server.requestCount)
        assertFalse(activity.isFinishing)

        activity.onWindowFocusChanged(true)
        awaitFinish { activity.isFinishing }

        assertEquals("Sent to your devices", awaitToast())
        assertEquals("from clipboard", f.cache.items.value.single().text)
        assertEquals(1, f.server.requestCount)
    }

    @Test
    fun emptyClipboardToastsAndFinishes() {
        setClipboard(null)

        val activity = Robolectric.buildActivity(ClipboardSendActivity::class.java).setup().get()
        activity.onWindowFocusChanged(true)

        assertTrue(activity.isFinishing)
        assertEquals("Clipboard is empty", ShadowToast.getTextOfLatestToast())
        assertEquals(0, f.server.requestCount)
    }
}
