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

        assertEquals("Sent to your devices", ShadowToast.getTextOfLatestToast())
        assertEquals(1, f.server.requestCount)
        assertEquals("shared", f.cache.items.value.single().text)
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

        assertEquals("Sent to your devices", ShadowToast.getTextOfLatestToast())
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
