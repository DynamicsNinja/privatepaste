package xyz.ficdev.privatepaste.service

import android.Manifest
import android.app.NotificationManager
import android.content.Intent
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.runBlocking
import okhttp3.mockwebserver.MockResponse
import org.junit.After
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
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import java.time.Instant

/** The app graph's session teardown, through the real Application. */
@RunWith(RobolectricTestRunner::class)
@Config(application = TestPrivatePasteApp::class)
class AppGraphTest {

    private val app = ApplicationProvider.getApplicationContext<TestPrivatePasteApp>()
    private val nm = app.getSystemService(NotificationManager::class.java)
    private lateinit var server: FakeServer

    @Before fun setUp() {
        shadowOf(app).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
        server = FakeServer()
    }

    @After fun tearDown() {
        app.shutDownForTest()
        server.shutdown()
    }

    private val clip = ClipDto(
        id = "c1", seq = 3, kind = ClipKind.TEXT, text = "a password", hash = "h-c1", sizeBytes = 10,
        sourceDeviceId = "pc", sourceDeviceName = "Laptop", lastCopiedAt = Instant.parse("2026-10-28T12:00:00Z"),
    )

    @Test
    fun signOutCancelsTheClipNotification() {
        app.signInTo(server)
        Notifications.postClip(app, clip)
        assertNotNull(shadowOf(nm).getNotification(Notifications.ID_CLIP))

        runBlocking { app.uiDeps.signOut() }

        assertNull("no clip preview survives the session", shadowOf(nm).getNotification(Notifications.ID_CLIP))
    }

    @Test
    fun revokeCancelsTheClipNotification() {
        server.ws = { MockResponse().setResponseCode(401) }
        app.signInTo(server)
        Notifications.postClip(app, clip)
        assertNotNull(shadowOf(nm).getNotification(Notifications.ID_CLIP))

        Robolectric.buildService(SyncService::class.java, Intent(app, SyncService::class.java)).create().startCommand(0, 1)
        awaitUntil("signed out") { app.uiDeps.session.value == null && app.graph.current.value == null }

        assertNull(shadowOf(nm).getNotification(Notifications.ID_CLIP))
    }
}
