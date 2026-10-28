package xyz.ficdev.privatepaste.service

import android.content.Intent
import androidx.test.core.app.ApplicationProvider
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(application = TestPrivatePasteApp::class)
class BootReceiverTest {

    private val app = ApplicationProvider.getApplicationContext<TestPrivatePasteApp>()
    private lateinit var server: FakeServer

    @Before fun setUp() {
        server = FakeServer()
    }

    @After fun tearDown() {
        app.shutDownForTest()
        server.shutdown()
    }

    private fun nextStarted() = shadowOf(app).nextStartedService?.component?.className

    @Test
    fun bootStartsServiceOnlyWhenSignedIn() {
        val receiver = BootReceiver()

        receiver.onReceive(app, Intent(Intent.ACTION_BOOT_COMPLETED))
        assertNull("signed out: nothing starts", nextStarted())

        app.signInTo(server)
        receiver.onReceive(app, Intent(Intent.ACTION_BOOT_COMPLETED))
        assertEquals(SyncService::class.java.name, nextStarted())
        receiver.onReceive(app, Intent(Intent.ACTION_MY_PACKAGE_REPLACED))
        assertEquals(SyncService::class.java.name, nextStarted())
        receiver.onReceive(app, Intent("xyz.example.OTHER"))
        assertNull(nextStarted())
    }

    @Test
    fun receiverIsRegisteredForBothBootActions() {
        for (action in listOf(Intent.ACTION_BOOT_COMPLETED, Intent.ACTION_MY_PACKAGE_REPLACED)) {
            val found = app.packageManager.queryBroadcastReceivers(Intent(action).setPackage(app.packageName), 0)
            assertTrue(action, found.any { it.activityInfo.name == BootReceiver::class.java.name })
        }
    }
}
