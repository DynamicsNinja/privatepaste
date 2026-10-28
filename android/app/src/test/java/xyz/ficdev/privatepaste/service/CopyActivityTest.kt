package xyz.ficdev.privatepaste.service

import android.Manifest
import android.app.NotificationManager
import android.content.ClipboardManager
import android.content.Intent
import androidx.test.core.app.ApplicationProvider
import okhttp3.mockwebserver.MockResponse
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowToast
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import java.time.Instant

@RunWith(RobolectricTestRunner::class)
@Config(application = TestPrivatePasteApp::class)
class CopyActivityTest {

    private val app = ApplicationProvider.getApplicationContext<TestPrivatePasteApp>()
    private val clipboard = app.getSystemService(ClipboardManager::class.java)
    private val nm = app.getSystemService(NotificationManager::class.java)
    private lateinit var server: FakeServer

    @Before fun setUp() {
        TestPrivatePasteApp.setUpHostProvider()
        shadowOf(app).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
        server = FakeServer()
        app.signInTo(server)
    }

    @After fun tearDown() {
        app.shutDownForTest()
        server.shutdown()
    }

    private fun clip(id: String, kind: ClipKind = ClipKind.TEXT, text: String? = "from the notification") = ClipDto(
        id = id, seq = 3, kind = kind, text = text, hash = "h-$id", width = 2, height = 2, sizeBytes = 4,
        sourceDeviceId = "pc", sourceDeviceName = "Laptop", lastCopiedAt = Instant.parse("2026-10-28T12:00:00Z"),
    )

    private fun runCopy(id: String?): String {
        val intent = Intent(app, CopyActivity::class.java).apply { if (id != null) putExtra(CopyActivity.EXTRA_CLIP_ID, id) }
        val controller = Robolectric.buildActivity(CopyActivity::class.java, intent).setup()
        awaitUntil("finished") { controller.get().isFinishing }
        return ShadowToast.getTextOfLatestToast() ?: throw AssertionError("no toast")
    }

    @Test
    fun copyActivityWritesAndFinishes() {
        val c = clip("c1")
        app.graph.cache.upsert(c, fromSync = true)
        Notifications.postClip(app, c)
        assertNotNull(shadowOf(nm).getNotification(Notifications.ID_CLIP))

        assertEquals("Copied", runCopy("c1"))
        assertEquals("from the notification", clipboard.primaryClip!!.getItemAt(0).text.toString())
        assertNull("the clip notification is cancelled", shadowOf(nm).getNotification(Notifications.ID_CLIP))
    }

    @Test
    fun copyActivityCopiesACachedImageOffline() {
        val png = byteArrayOf(1, 2, 3, 4)
        app.graph.cache.upsert(clip("i1", ClipKind.IMAGE, text = null), fromSync = true)
        app.graph.imageFiles.store("i1", png)
        server.shutdown()   // offline: the file the applier kept is used

        assertEquals("Copied", runCopy("i1"))
        val uri = clipboard.primaryClip!!.getItemAt(0).uri
        assertArrayEquals(png, app.contentResolver.openInputStream(uri)!!.use { it.readBytes() })
    }

    @Test
    fun unknownClipToastsAndFinishes() {
        val c = clip("c1")
        Notifications.postClip(app, c)

        assertEquals("Couldn't copy the clip.", runCopy("gone"))
        assertNull(shadowOf(nm).getNotification(Notifications.ID_CLIP))
        assertEquals("Couldn't copy the clip.", runCopy(null))
    }

    @Test
    fun unauthorizedImageFetchRevokesAndStillFinishes() {
        server.other = { r -> if (r.path == "/api/clips/i9/image") MockResponse().setResponseCode(401) else null }
        app.graph.cache.upsert(clip("i9", ClipKind.IMAGE, text = null), fromSync = true)
        Notifications.postClip(app, clip("i9", ClipKind.IMAGE, text = null))

        assertEquals("Couldn't copy the clip.", runCopy("i9"))
        assertNull(shadowOf(nm).getNotification(Notifications.ID_CLIP))
        awaitUntil("the 401 signs the device out") { app.uiDeps.session.value == null }
        awaitUntil("alert") { app.graph.signedOutAlerts == 1 }
    }
}
