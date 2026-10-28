package xyz.ficdev.privatepaste.ui

import android.Manifest
import android.app.Application
import android.content.Intent
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import xyz.ficdev.privatepaste.MainActivity
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.clipboard.ClipImageFiles
import xyz.ficdev.privatepaste.clipboard.ClipboardWriter
import xyz.ficdev.privatepaste.storage.DeliveryMode
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.SettingsStore
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import xyz.ficdev.privatepaste.sync.SignInResult
import xyz.ficdev.privatepaste.sync.SyncState
import java.io.File

/** Test Application: Task 6's PrivatePasteApp implements [UiDepsProvider] the same way. */
class FakeUiApp : Application(), UiDepsProvider {
    lateinit var fake: FakeUiDeps
    override val uiDeps: UiDeps get() = fake
}

class FakeUiDeps(app: Application) : UiDeps {
    val sessionFlow = MutableStateFlow<StoredSession?>(null)
    var signOuts = 0
    var servicesStarted = 0

    override val session: StateFlow<StoredSession?> = sessionFlow
    override suspend fun signIn(url: String, password: String, deviceName: String): SignInResult = SignInResult.WrongPassword
    override suspend fun signOut() {
        signOuts++
        sessionFlow.value = null
    }
    override val lastServerUrl: String? = "https://paste.example.com"
    var previousDeviceName: String? = null
    override val lastDeviceName: String? get() = previousDeviceName
    override val syncState: StateFlow<SyncState> = MutableStateFlow(SyncState.CONNECTED)
    override val cache: HistoryCache? = null
    override val thumbs: ThumbnailStore? = null
    override fun api(): PrivatePasteApi? = null
    override val clipboard = ClipboardWriter(app, ClipImageFiles(File(app.cacheDir, "clipboard")))
    override val settings = SettingsStore(app)
    override val startService: () -> Unit = { servicesStarted++ }
    override val isBatteryExempt: () -> Boolean = { false }
    var notificationsEnabled = true
    override val areNotificationsEnabled: () -> Boolean = { notificationsEnabled }
    override val requestBatteryExemption: () -> Intent = { Intent("test.REQUEST_BATTERY") }
    override val appVersion: String = "0.1.0"
}

@RunWith(RobolectricTestRunner::class)
@Config(application = FakeUiApp::class)
class AppNavTest {

    @get:Rule val compose = createEmptyComposeRule()

    private val app = ApplicationProvider.getApplicationContext<FakeUiApp>()
    private val deps = FakeUiDeps(app).also { app.fake = it }
    private var scenario: ActivityScenario<MainActivity>? = null

    private val session = StoredSession("https://paste.example.com/", "dev-1", "Pixel 9", "token")

    private fun launch() {
        scenario = ActivityScenario.launch(MainActivity::class.java)
    }

    @After fun tearDown() {
        scenario?.close()
    }

    @Test
    fun signedOutShowsSignInPrefilled() {
        launch()
        compose.onNodeWithText("Server address").assertIsDisplayed()
        compose.onNodeWithText("Password").assertIsDisplayed()
        compose.onNodeWithText("Device name").assertIsDisplayed()
        compose.onNodeWithText("https://paste.example.com").assertIsDisplayed()
        compose.onNodeWithText("Sign in").performScrollTo().assertIsDisplayed().assertIsNotEnabled()   // no password yet
    }

    @Test
    fun signInPrefillsThePreviousDeviceName() {
        deps.previousDeviceName = "Ivan's phone"
        launch()
        compose.onNodeWithText("Ivan's phone").assertIsDisplayed()
    }

    @Test
    fun signInFallsBackToTheModelName() {
        launch()
        compose.onNodeWithText(android.os.Build.MODEL).assertIsDisplayed()
    }

    @Test
    fun notificationDeliveryWithNotificationsOffSaysSoAndOpensSettings() {
        deps.sessionFlow.value = session
        deps.settings.onboardingDone = true
        deps.settings.deliveryMode = DeliveryMode.NOTIFICATION
        deps.notificationsEnabled = false
        launch()

        compose.onNodeWithContentDescription("Settings").performClick()
        compose.onNodeWithText("Notifications are off, so clips won't show up.").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("Open settings").performScrollTo().performClick()

        val started = shadowOf(app).nextStartedActivity
        assertEquals(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS, started.action)
        assertEquals(app.packageName, started.getStringExtra(android.provider.Settings.EXTRA_APP_PACKAGE))

        compose.onNodeWithText("Copy automatically").performScrollTo().performClick()
        compose.onNodeWithText("Notifications are off, so clips won't show up.").assertDoesNotExist()
    }

    @Test
    fun firstSignInRunsOnboardingThenHistory() {
        shadowOf(app).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
        deps.sessionFlow.value = session
        deps.settings.onboardingDone = false
        launch()

        compose.onNodeWithText("Allow PrivatePaste to run in the background").assertIsDisplayed()
        compose.onNodeWithText("Allow").assertIsDisplayed()
        compose.onNodeWithText("Not now").performClick()

        compose.onNodeWithText("Nothing here yet. Copy something on your computer and it shows up here.").assertIsDisplayed()
        assertTrue(deps.settings.onboardingDone)
        assertEquals(1, deps.servicesStarted)
    }

    @Test
    fun notificationStepComesFirstWhenNotGranted() {
        deps.sessionFlow.value = session
        deps.settings.onboardingDone = false
        launch()
        compose.onNodeWithText("PrivatePaste shows a quiet notification while it's connected").assertIsDisplayed()
    }

    @Test
    fun settingsShowsAccountAndSignsOut() {
        deps.sessionFlow.value = session
        deps.settings.onboardingDone = true
        launch()

        compose.onNodeWithContentDescription("Settings").performClick()
        compose.onNodeWithText("https://paste.example.com/").assertIsDisplayed()
        compose.onNodeWithText("Pixel 9").assertIsDisplayed()
        compose.onNodeWithText("Copy automatically").assertIsDisplayed()
        compose.onNodeWithText("Show a notification with a Copy button").assertIsDisplayed()
        compose.onNodeWithText("Restricted").assertExists()
        compose.onNodeWithText("Version 0.1.0").assertExists()

        compose.onNodeWithText("Show a notification with a Copy button").performScrollTo().performClick()
        compose.waitForIdle()
        assertEquals(DeliveryMode.NOTIFICATION, deps.settings.deliveryMode)

        compose.onNodeWithText("Sign out").performScrollTo().performClick()
        compose.onNodeWithText("Sign out of this phone? Local history is removed.").assertIsDisplayed()
        compose.onNodeWithText("Cancel").performClick()
        assertEquals(0, deps.signOuts)
    }
}
