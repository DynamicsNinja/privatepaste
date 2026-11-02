package xyz.ficdev.privatepaste.service

import android.Manifest
import android.app.NotificationManager
import android.content.Intent
import androidx.test.core.app.ApplicationProvider
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
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
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(application = TestPrivatePasteApp::class)
class SaveActivityTest {
    @get:Rule val tmp = TemporaryFolder()

    private val app = ApplicationProvider.getApplicationContext<TestPrivatePasteApp>()
    private val nm = app.getSystemService(NotificationManager::class.java)
    private lateinit var server: FakeServer
    private val saved = mutableListOf<Pair<String, ByteArray>>()

    @Before fun setUp() {
        TestPrivatePasteApp.setUpHostProvider()
        shadowOf(app).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
        server = FakeServer()
        app.signInTo(server)
        TestPrivatePasteApp.saver = { files -> saved += files.map { it.name to it.readBytes() }; true }
    }

    @After fun tearDown() {
        TestPrivatePasteApp.saver = { true }
        app.shutDownForTest()
        server.shutdown()
    }

    private fun runSave(id: String?): String {
        val intent = Intent(app, SaveActivity::class.java).apply { if (id != null) putExtra(SaveActivity.EXTRA_CLIP_ID, id) }
        val controller = Robolectric.buildActivity(SaveActivity::class.java, intent).setup()
        awaitUntil("finished") { controller.get().isFinishing }
        return ShadowToast.getTextOfLatestToast() ?: throw AssertionError("no toast")
    }

    @Test
    fun saveDownloadsTheFilesHandsThemToTheSaverAndToasts() {
        val clip = server.fileClip("f1", tmp.root, "../report.pdf" to byteArrayOf(1, 2), "CON.txt" to byteArrayOf(3))
        app.graph.cache.upsert(clip, fromSync = true)
        Notifications.postClip(app, clip, copy = false, save = true)
        assertNotNull(shadowOf(nm).getNotification(Notifications.ID_CLIP))

        assertEquals("Saved to Downloads", runSave("f1"))

        assertEquals(listOf("report.pdf", "_CON.txt"), saved.map { it.first })
        assertTrue(byteArrayOf(1, 2).contentEquals(saved[0].second))
        assertEquals(2, server.blobFetches.get())
        assertNull("the clip notification is cancelled", shadowOf(nm).getNotification(Notifications.ID_CLIP))
        assertTrue("kept for Copy and Share", File(app.cacheDir, "files").walkTopDown().any { it.name == "report.pdf" })
    }

    @Test
    fun aFailedSaveToastsFailure() {
        TestPrivatePasteApp.saver = { false }
        app.graph.cache.upsert(server.fileClip("f1", tmp.root, "a.txt" to byteArrayOf(1)), fromSync = true)

        assertEquals("Couldn't save the files", runSave("f1"))
    }

    @Test
    fun anUnknownOrNonFileClipToastsFailure() {
        assertEquals("Couldn't save the files", runSave("gone"))
        assertEquals("Couldn't save the files", runSave(null))
        assertTrue(saved.isEmpty())
    }
}
