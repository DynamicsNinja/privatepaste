package xyz.ficdev.privatepaste.service

import android.Manifest
import android.content.Intent
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import xyz.ficdev.privatepaste.MainActivity
import xyz.ficdev.privatepaste.sync.SyncState

/** Step 5: the real app launches, the service runs against a local WS server, and sync reaches CONNECTED. */
@RunWith(RobolectricTestRunner::class)
@Config(application = TestPrivatePasteApp::class)
class LaunchSmokeTest {

    @get:Rule val compose = createEmptyComposeRule()

    private val app = ApplicationProvider.getApplicationContext<TestPrivatePasteApp>()
    private lateinit var server: FakeServer
    private var scenario: ActivityScenario<MainActivity>? = null

    @Before fun setUp() {
        server = FakeServer()
        shadowOf(app).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
    }

    @After fun tearDown() {
        scenario?.close()
        app.shutDownForTest()
        server.shutdown()
    }

    @Test
    fun signedOutLaunchShowsSignIn() {
        scenario = ActivityScenario.launch(MainActivity::class.java)
        compose.onNodeWithText("Server address").assertIsDisplayed()
        assertEquals(null, shadowOf(app).nextStartedService)
    }

    @Test
    fun launchConnectsEndToEnd() {
        app.signInTo(server)
        app.uiDeps.settings.onboardingDone = true

        scenario = ActivityScenario.launch(MainActivity::class.java)
        compose.onNodeWithText("Nothing here yet. Copy something on your computer and it shows up here.").assertIsDisplayed()

        // Opening the app (signed in) asks for the service; run it as the system would.
        val started = shadowOf(app).nextStartedService ?: throw AssertionError("service not started")
        assertEquals(SyncService::class.java.name, started.component?.className)
        Robolectric.buildService(SyncService::class.java, Intent(started)).create().startCommand(0, 1)

        awaitUntil("connected") { app.uiDeps.syncState.value == SyncState.CONNECTED }
        compose.onNodeWithText("Connected").assertIsDisplayed()
        assertEquals(1, server.wsRequests.get())
    }
}
