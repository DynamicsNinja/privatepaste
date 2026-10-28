package xyz.ficdev.privatepaste.service

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import androidx.test.core.app.ApplicationProvider
import okhttp3.mockwebserver.MockResponse
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.android.controller.ServiceController
import org.robolectric.annotation.Config
import xyz.ficdev.privatepaste.MainActivity
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.sync.SyncState

@RunWith(RobolectricTestRunner::class)
@Config(application = TestPrivatePasteApp::class)
class SyncServiceTest {

    private val app = ApplicationProvider.getApplicationContext<TestPrivatePasteApp>()
    private val nm = app.getSystemService(NotificationManager::class.java)
    private lateinit var server: FakeServer
    private var controller: ServiceController<SyncService>? = null

    @Before fun setUp() {
        server = FakeServer()
        shadowOf(app).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
    }

    @After fun tearDown() {
        app.shutDownForTest()
        server.shutdown()
    }

    private fun startService(): SyncService {
        val intent = Intent(app, SyncService::class.java).setAction(SyncService.ACTION_START)
        val c = Robolectric.buildService(SyncService::class.java, intent).create().startCommand(0, 1)
        controller = c
        return c.get()
    }

    private fun connectionNotification(): Notification? = shadowOf(nm).getNotification(Notifications.ID_CONNECTION)

    private fun Notification.title() = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()

    @Test
    fun serviceStartsForegroundWithConnectionNotification() {
        server.ws = { MockResponse().setResponseCode(503) }   // keep the engine from connecting during the checks
        app.signInTo(server)
        awaitUntil("not started yet reads as connecting") { app.uiDeps.syncState.value == SyncState.CONNECTING }
        val service = startService()

        val shadow = shadowOf(service)
        assertEquals(Notifications.ID_CONNECTION, shadow.lastForegroundNotificationId)
        val n = shadow.lastForegroundNotification ?: throw AssertionError("no foreground notification")
        assertEquals(Notifications.CHANNEL_CONNECTION, n.channelId)
        assertEquals("the engine has not tried yet: never 'Offline'", "Connecting…", n.title())
        assertTrue("ongoing", n.flags and Notification.FLAG_ONGOING_EVENT != 0)
        assertEquals(R.drawable.ic_notification, n.smallIcon.resId)
        val tap = shadowOf(n.contentIntent)
        assertTrue(tap.isActivity)
        assertEquals(MainActivity::class.java.name, tap.savedIntent.component?.className)
        assertFalse(shadow.isStoppedBySelf)

        // Idempotent: a second start is harmless and keeps the service sticky.
        assertEquals(Service.START_STICKY, service.onStartCommand(Intent(app, SyncService::class.java), 0, 2))
        assertFalse(shadow.isStoppedBySelf)

        // Channels exactly as specified.
        assertEquals(NotificationManager.IMPORTANCE_MIN, nm.getNotificationChannel(Notifications.CHANNEL_CONNECTION).importance)
        assertEquals(NotificationManager.IMPORTANCE_HIGH, nm.getNotificationChannel(Notifications.CHANNEL_CLIPS).importance)
        assertEquals(NotificationManager.IMPORTANCE_DEFAULT, nm.getNotificationChannel(Notifications.CHANNEL_ALERTS).importance)
    }

    @Test
    fun serviceWithoutSessionStopsItself() {
        val service = startService()
        assertNotNull(shadowOf(service).lastForegroundNotification)   // startForeground first, as the contract requires
        assertTrue(shadowOf(service).isStoppedBySelf)
        assertEquals(Service.START_NOT_STICKY, service.onStartCommand(null, 0, 2))
    }

    @Test
    fun notificationTextFollowsState() {
        assertEquals("PrivatePaste connected", Notifications.connectionText(SyncState.CONNECTED))
        assertEquals("Connecting…", Notifications.connectionText(SyncState.CONNECTING))
        assertEquals("Offline, retrying", Notifications.connectionText(SyncState.OFFLINE))

        app.signInTo(server)
        startService()
        awaitUntil("connected") { connectionNotification()?.title() == "PrivatePaste connected" }
        assertEquals(SyncState.CONNECTED, app.uiDeps.syncState.value)

        // The server goes away: the notification says so.
        server.ws = { MockResponse().setResponseCode(503) }
        server.shutdown()
        awaitUntil("offline") { connectionNotification()?.title() == "Offline, retrying" }
    }

    @Test
    fun revokedStopsServiceAndNotifiesOnce() {
        server.ws = { MockResponse().setResponseCode(401) }
        assertRevokedOnce()
    }

    @Test
    fun close1008StopsServiceAndNotifiesOnce() {
        server.ws = { MockResponse().withWebSocketUpgrade(FakeServer.CloseListener(1008)) }
        assertRevokedOnce()
    }

    /** Signs in, starts the service, lets the server revoke, and checks the whole revoke path ran exactly once. */
    private fun assertRevokedOnce() {
        app.signInTo(server)
        app.graph.imageFiles.store("img-1", byteArrayOf(1, 2, 3))
        val service = startService()

        awaitUntil("signed out") { app.uiDeps.session.value == null }
        awaitUntil("service stopped") { shadowOf(service).isStoppedBySelf }
        idleFor(1_500)   // a retry or a second alert would show up here

        assertEquals("the revoke is handled (and the alert posted) once", 1, app.graph.signedOutAlerts)
        val alerts = shadowOf(nm).allNotifications.filter { it.channelId == Notifications.CHANNEL_ALERTS }
        assertEquals(1, alerts.size)
        assertEquals("Signed out. Sign in again to keep syncing.", alerts.single().extras.getCharSequence(Notification.EXTRA_TEXT)?.toString())
        assertEquals("never reconnects after a revoke", 1, server.wsRequests.get())
        assertEquals(SyncState.SIGNED_OUT, app.uiDeps.syncState.value)   // settled during idleFor
        assertNull(app.uiDeps.api())
        assertNull(app.uiDeps.cache)
        assertNull(app.clipSender)
        assertNull("plaintext clipboard images are deleted", app.graph.imageFiles.find("img-1"))
        assertEquals(server.url, app.uiDeps.lastServerUrl)   // the sign-in form is prefilled
    }

    @Test
    fun signOutStopsEngineAndService() {
        app.signInTo(server)
        app.graph.imageFiles.store("img-1", byteArrayOf(1, 2, 3))
        val service = startService()
        awaitUntil("connected") { app.uiDeps.syncState.value == SyncState.CONNECTED }

        kotlinx.coroutines.runBlocking { app.uiDeps.signOut() }

        assertNull(app.uiDeps.session.value)
        assertNull(app.uiDeps.api())
        assertNull("plaintext clipboard images are deleted", app.graph.imageFiles.find("img-1"))
        awaitUntil("signed-out state") { app.uiDeps.syncState.value == SyncState.SIGNED_OUT }
        awaitUntil("service stopped") { shadowOf(service).isStoppedBySelf }
        assertEquals("stopped by its last start id", 1, shadowOf(service).stopSelfResultId)
        val alerts = shadowOf(nm).allNotifications.filter { it.channelId == Notifications.CHANNEL_ALERTS }
        assertTrue("a deliberate sign-out posts no alert", alerts.isEmpty())
    }

    @Test
    fun stopServiceStopsEngine() {
        app.signInTo(server)
        startService()
        awaitUntil("connected") { app.uiDeps.syncState.value == SyncState.CONNECTED }

        controller!!.destroy()
        assertNull("no connection notification after the service", connectionNotification())

        awaitUntil("engine stopped") { app.uiDeps.syncState.value == SyncState.OFFLINE }
        idleFor(1_500)
        assertEquals("no reconnect without the service", SyncState.OFFLINE, app.uiDeps.syncState.value)
        assertEquals(1, server.wsRequests.get())
    }

    @Test
    fun pendingStartSurvivesSignOut() {
        app.signInTo(server)
        val service = startService()   // start id 1
        val shadow = shadowOf(service)

        // A second startForegroundService is queued (start id 2) when the session ends; its onStartCommand has not run.
        kotlinx.coroutines.runBlocking { app.uiDeps.signOut() }
        idleFor(200)

        assertNull("never stopService: it would drop the pending start and crash", shadowOf(app).nextStoppedService)
        assertEquals("stops only up to start id 1; the system keeps the service for id 2", 1, shadow.stopSelfResultId)

        // The pending start arrives: it is answered with startForeground, then stops by its own id.
        assertEquals(Service.START_NOT_STICKY, service.onStartCommand(Intent(app, SyncService::class.java), 0, 2))
        assertEquals(Notifications.ID_CONNECTION, shadow.lastForegroundNotificationId)
        assertEquals(2, shadow.stopSelfId)
    }
}
