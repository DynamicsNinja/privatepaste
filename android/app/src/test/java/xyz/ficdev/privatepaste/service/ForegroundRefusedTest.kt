package xyz.ficdev.privatepaste.service

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import okhttp3.mockwebserver.MockResponse
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import xyz.ficdev.privatepaste.MainActivity

/** Android 12+ refuses startForeground from the background (ForegroundServiceStartNotAllowedException). */
class RefusingSyncService : SyncService() {
    override fun enterForeground(n: Notification) = throw IllegalStateException("not allowed now")
}

@RunWith(RobolectricTestRunner::class)
@Config(application = TestPrivatePasteApp::class)
class ForegroundRefusedTest {

    private val app = ApplicationProvider.getApplicationContext<TestPrivatePasteApp>()
    private val nm = app.getSystemService(NotificationManager::class.java)
    private lateinit var server: FakeServer

    @Before fun setUp() {
        shadowOf(app).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
        server = FakeServer()
        server.ws = { MockResponse().setResponseCode(503) }
    }

    @After fun tearDown() {
        app.shutDownForTest()
        server.shutdown()
    }

    private fun alerts() = shadowOf(nm).allNotifications.filter { it.channelId == Notifications.CHANNEL_ALERTS }

    private fun refusedStart(startId: Int): Int {
        val service = Robolectric.buildService(RefusingSyncService::class.java, Intent(app, RefusingSyncService::class.java)).create().get()
        return service.onStartCommand(Intent(app, RefusingSyncService::class.java), 0, startId)
    }

    @Test
    fun refusedStartWithASessionPostsSyncPausedOnce() {
        app.signInTo(server)

        assertEquals(Service.START_NOT_STICKY, refusedStart(1))
        assertEquals(Service.START_NOT_STICKY, refusedStart(2))   // e.g. the next boot or network attempt

        val posted = alerts()
        assertEquals("one notification, fixed id", 1, posted.size)
        assertEquals(
            "Sync paused. Open PrivatePaste to resume.",
            posted.single().extras.getCharSequence(Notification.EXTRA_TEXT)?.toString(),
        )
        assertTrue(shadowOf(nm).getNotification(Notifications.ID_SYNC_PAUSED) != null)
    }

    @Test
    fun refusedStartWithoutASessionPostsNothing() {
        refusedStart(1)
        assertTrue(alerts().isEmpty())
    }

    @Test
    fun openingTheAppCancelsSyncPaused() {
        app.signInTo(server)
        refusedStart(1)
        assertEquals(1, alerts().size)

        ActivityScenario.launch(MainActivity::class.java).use {
            assertNull(shadowOf(nm).getNotification(Notifications.ID_SYNC_PAUSED))
        }
    }
}
