package xyz.ficdev.privatepaste.service

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ClipboardManager
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.clipboard.ClipImageFiles
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.storage.DeliveryMode
import java.io.File
import java.time.Instant

@RunWith(RobolectricTestRunner::class)
@Config(application = TestPrivatePasteApp::class)
class ServiceClipApplierTest {

    private val app = ApplicationProvider.getApplicationContext<TestPrivatePasteApp>()
    private val clipboard = app.getSystemService(ClipboardManager::class.java)
    private val nm = app.getSystemService(NotificationManager::class.java)
    private val files = ClipImageFiles(File(app.cacheDir, "clipboard"))
    private val png = byteArrayOf(0x89.toByte(), 'P'.code.toByte(), 'N'.code.toByte(), 'G'.code.toByte(), 7)

    private var mode = DeliveryMode.AUTOMATIC
    private val fetched = mutableListOf<String>()
    private var fetch: suspend (String) -> ByteArray = { id -> fetched += id; png }

    private fun applier() = ServiceClipApplier(app, { mode }, app.clipboardWriter(files), files) { fetch(it) }

    private fun text(id: String, text: String, device: String? = "Laptop") = ClipDto(
        id = id, seq = 1, kind = ClipKind.TEXT, text = text, hash = "h-$id", sizeBytes = text.length.toLong(),
        sourceDeviceId = "pc", sourceDeviceName = device, lastCopiedAt = Instant.parse("2026-10-28T12:00:00Z"),
    )

    private fun image(id: String) = ClipDto(
        id = id, seq = 2, kind = ClipKind.IMAGE, hash = "h-$id", width = 640, height = 480, sizeBytes = 5,
        sourceDeviceId = "pc", sourceDeviceName = "Laptop", lastCopiedAt = Instant.parse("2026-10-28T12:00:00Z"),
    )

    @Before fun setUp() {
        TestPrivatePasteApp.setUpHostProvider()
        shadowOf(app).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
    }

    @Test
    fun automaticModeWritesClipboard() = runBlocking {
        assertTrue(applier().apply(text("c1", "hello from the PC")))
        assertEquals("hello from the PC", clipboard.primaryClip!!.getItemAt(0).text.toString())

        assertTrue(applier().apply(image("i1")))
        assertEquals(listOf("i1"), fetched)
        val uri = clipboard.primaryClip!!.getItemAt(0).uri
        assertEquals("content", uri.scheme)
        assertArrayEquals(png, app.contentResolver.openInputStream(uri)!!.use { it.readBytes() })
        assertTrue("no clip notification in automatic mode", shadowOf(nm).allNotifications.none { it.channelId == Notifications.CHANNEL_CLIPS })
    }

    @Test
    fun failedImageFetchReturnsFalseInBothModes() = runBlocking {
        fetch = { throw ApiException.Transient("down") }
        clipboard.setPrimaryClip(android.content.ClipData.newPlainText("x", "before"))
        assertFalse(applier().apply(image("i1")))
        assertEquals("before", clipboard.primaryClip!!.getItemAt(0).text.toString())

        mode = DeliveryMode.NOTIFICATION
        assertFalse(applier().apply(image("i2")))
        assertNull(shadowOf(nm).getNotification(Notifications.ID_CLIP))
    }

    @Test
    fun unauthorizedImageFetchPropagates() = runBlocking {
        fetch = { throw ApiException.Unauthorized() }
        try {
            applier().apply(image("i1"))
            throw AssertionError("expected Unauthorized")
        } catch (_: ApiException.Unauthorized) {
        }
    }

    @Test
    fun notificationTitleNamesThePersonWhenKnown() = runBlocking {
        mode = DeliveryMode.NOTIFICATION
        assertTrue(applier().apply(text("c2", "hi").copy(sourceUserName = "Ana")))
        val n = shadowOf(nm).getNotification(Notifications.ID_CLIP) ?: throw AssertionError("no clip notification")
        assertEquals("New clip from Ana · Laptop", n.extras.getCharSequence(Notification.EXTRA_TITLE).toString())
    }

    @Test
    fun notificationModePostsCopyAction() = runBlocking {
        val applier = applier()
        mode = DeliveryMode.NOTIFICATION   // read on every apply
        clipboard.setPrimaryClip(android.content.ClipData.newPlainText("x", "before"))

        assertTrue(applier.apply(text("c1", "x".repeat(150))))
        assertEquals("the clipboard is left alone", "before", clipboard.primaryClip!!.getItemAt(0).text.toString())
        val n = shadowOf(nm).getNotification(Notifications.ID_CLIP) ?: throw AssertionError("no clip notification")
        assertEquals(Notifications.CHANNEL_CLIPS, n.channelId)
        assertEquals("New clip from Laptop", n.extras.getCharSequence(Notification.EXTRA_TITLE).toString())
        assertEquals("x".repeat(100), n.extras.getCharSequence(Notification.EXTRA_TEXT).toString())
        val action = n.actions.single()
        assertEquals("Copy", action.title.toString())
        val pending = shadowOf(action.actionIntent)
        assertTrue(pending.isActivity)
        assertTrue("immutable", pending.flags and PendingIntent.FLAG_IMMUTABLE != 0)
        assertEquals(CopyActivity::class.java.name, pending.savedIntent.component?.className)
        assertEquals("c1", pending.savedIntent.getStringExtra(CopyActivity.EXTRA_CLIP_ID))

        // The next clip replaces it (one notification id), and an image says its size.
        assertTrue(applier.apply(image("i1")))
        val clips = shadowOf(nm).allNotifications.filter { it.channelId == Notifications.CHANNEL_CLIPS }
        assertEquals(1, clips.size)
        assertEquals("Image 640×480", clips.single().extras.getCharSequence(Notification.EXTRA_TEXT).toString())
        assertEquals("i1", shadowOf(clips.single().actions.single().actionIntent).savedIntent.getStringExtra(CopyActivity.EXTRA_CLIP_ID))
        assertArrayEquals("the image is kept for the Copy action", png, files.find("i1")!!.readBytes())

        // Switching back takes effect on the next clip.
        mode = DeliveryMode.AUTOMATIC
        assertTrue(applier.apply(text("c2", "now automatic")))
        assertEquals("now automatic", clipboard.primaryClip!!.getItemAt(0).text.toString())
    }

    @Test
    fun notificationModeWithNotificationsDeniedReturnsFalse() = runBlocking {
        shadowOf(app).denyPermissions(Manifest.permission.POST_NOTIFICATIONS)
        mode = DeliveryMode.NOTIFICATION
        assertFalse(applier().apply(text("c1", "hi")))
    }
}
