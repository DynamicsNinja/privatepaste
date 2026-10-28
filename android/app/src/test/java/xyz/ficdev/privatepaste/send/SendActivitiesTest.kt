package xyz.ficdev.privatepaste.send

import android.app.Application
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Looper
import androidx.test.core.app.ApplicationProvider
import org.junit.After
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
import org.robolectric.shadows.ShadowToast
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
        assertTrue(f.server.takeRequest().body.readUtf8().contains("bold words"))
    }

    @Test
    fun shareActivityImagePathSendsBytesFromContentUri() {
        f.respondWith { f.json(f.clipJson("i", "")) }
        val uri = android.net.Uri.parse("content://media/external/images/media/7")
        val bytes = ByteArray(200) { (it * 3).toByte() }
        val app = ApplicationProvider.getApplicationContext<Application>()
        shadowOf(app.contentResolver).registerInputStream(uri, java.io.ByteArrayInputStream(bytes))
        val intent = Intent(Intent.ACTION_SEND).setType("image/png").putExtra(Intent.EXTRA_STREAM, uri)

        val controller = Robolectric.buildActivity(ShareActivity::class.java, intent).setup()
        awaitFinish { controller.get().isFinishing }

        assertEquals("Sent to your devices", awaitToast())
        val req = f.server.takeRequest()
        assertEquals("/api/clips/image", req.path)
        assertTrue(req.body.readByteString().indexOf(okio.ByteString.of(*bytes)) >= 0)
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
