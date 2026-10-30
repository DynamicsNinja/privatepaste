package xyz.ficdev.privatepaste.ui

import android.graphics.Bitmap
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.test.captureToImage
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onRoot
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.storage.DeliveryMode
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.ui.history.HISTORY_TOP_BAR_TAG
import xyz.ficdev.privatepaste.ui.history.HistoryContent
import xyz.ficdev.privatepaste.ui.history.HistoryState
import xyz.ficdev.privatepaste.ui.history.HistoryTab
import xyz.ficdev.privatepaste.ui.settings.SettingsContent
import xyz.ficdev.privatepaste.ui.settings.SettingsState
import xyz.ficdev.privatepaste.ui.signin.SignInContent
import xyz.ficdev.privatepaste.ui.signin.SignInViewModel
import xyz.ficdev.privatepaste.ui.theme.PrivatePasteTheme
import java.io.File
import java.time.Clock
import java.time.Instant
import java.time.ZoneId
import java.util.Locale

/**
 * Saves PNGs of the three screens (plus a close-up of the history top bar), dark and light, into the directory named by PP_SCREENSHOT_DIR
 * (e.g. `android/app/build/screenshots`). Skipped when the variable is not set.
 */
@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(qualifiers = "w411dp-h891dp-xxhdpi")
class ScreenshotTest {

    @get:Rule val compose = createComposeRule()

    private val dir = System.getenv("PP_SCREENSHOT_DIR")?.takeIf { it.isNotBlank() }?.let(::File)

    private val now = Instant.parse("2026-10-28T10:00:00Z")
    private val time = RelativeTime(Clock.fixed(now, ZoneId.of("UTC")), ZoneId.of("UTC"), Locale.ENGLISH)

    private fun clip(id: String, kind: ClipKind, text: String?, minutesAgo: Long, device: String, platform: String?) = ClipDto(
        id = id, seq = 100 - minutesAgo, kind = kind, text = text, hash = "h-$id", sizeBytes = 10,
        sourceDeviceId = device, sourceDeviceName = device, sourceDevicePlatform = platform,
        lastCopiedAt = now.minusSeconds(minutesAgo * 60),
    )

    private val history = HistoryState(
        items = listOf(
            clip("1", ClipKind.TEXT, "ssh deploy@build-01 'systemctl restart privatepaste'", 0, "Workstation", "windows"),
            clip("2", ClipKind.LINK, "https://github.com/ficdev/privatepaste/releases", 12, "MacBook", "macos")
                .copy(pinnedAt = now.toString()),
            clip("3", ClipKind.IMAGE, null, 95, "ThinkPad", "linux"),
            clip("4", ClipKind.TEXT, "Meeting notes:\nship the Android client", 60 * 26, "Pixel 9", "android"),
            clip("5", ClipKind.TEXT, "Grocery list: oat milk, eggs, basil", 60 * 30, "Web", "web"),
            clip("6", ClipKind.TEXT, "4111 1111 1111 1111", 60 * 24 * 5, "Old phone", null),
        ),
        liveClipId = "1",
        syncState = SyncState.CONNECTED,
    )

    // Two people on one server: the person chip shows on every row.
    private val people = history.copy(
        items = history.items.mapIndexed { i, c -> c.copy(sourceUserName = if (i % 2 == 0) "Ana" else "Ben") },
    )

    private val pinnedTab = HistoryState(
        tab = HistoryTab.PINNED,
        items = history.items.filter { it.id in setOf("2", "4", "5") }
            .map { it.copy(pinnedAt = now.toString()) },
        syncState = SyncState.CONNECTED,
    )

    // createComposeRule allows one setContent per test, so each screen/theme pair is its own test.
    @Test fun signInDark() = shootOne("signin", dark = true) { SignInSample() }
    @Test fun signInLight() = shootOne("signin", dark = false) { SignInSample() }
    @Test fun historyDark() = shootOne("history", dark = true) { HistorySample() }
    @Test fun historyLight() = shootOne("history", dark = false) { HistorySample() }
    @Test fun historyPeopleDark() = shootOne("history-people", dark = true) { HistorySample(people) }
    @Test fun historyPeopleLight() = shootOne("history-people", dark = false) { HistorySample(people) }
    // Close-ups of the history top bar (brand mark, connection chip, settings button).
    @Test fun historyPinnedDark() = shootOne("history-pinned", dark = true) { HistorySample(pinnedTab) }
    @Test fun historyPinnedLight() = shootOne("history-pinned", dark = false) { HistorySample(pinnedTab) }
    @Test fun historyHeaderDark() = shootOne("history-header", dark = true, tag = HISTORY_TOP_BAR_TAG) { HistorySample() }
    @Test fun historyHeaderLight() = shootOne("history-header", dark = false, tag = HISTORY_TOP_BAR_TAG) { HistorySample() }
    @Test fun settingsDark() = shootOne("settings", dark = true) { SettingsSample() }
    @Test fun settingsLight() = shootOne("settings", dark = false) { SettingsSample() }

    private fun shootOne(name: String, dark: Boolean, tag: String? = null, content: @Composable () -> Unit) {
        assumeTrue("PP_SCREENSHOT_DIR not set", dir != null)
        compose.setContent { PrivatePasteTheme(darkTheme = dark) { content() } }
        compose.waitForIdle()
        val node = if (tag == null) compose.onRoot() else compose.onNodeWithTag(tag)
        val bitmap = node.captureToImage().asAndroidBitmap()
        dir!!.mkdirs()
        File(dir, "$name-${if (dark) "dark" else "light"}.png").outputStream().use {
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, it)
        }
    }

    @Composable
    private fun SignInSample() = SignInContent(
        state = SignInViewModel.State(url = "https://paste.example.com", username = "Ana", deviceName = "Pixel 9"),
        onUrlChange = {}, onUsernameChange = {}, onPasswordChange = {}, onDeviceNameChange = {}, onSubmit = {},
    )

    @Composable
    private fun HistorySample(state: HistoryState = history) = HistoryContent(
        state = state, relativeTime = time,
        onQueryChange = {}, onCopy = {}, onDelete = {}, onNearEnd = {}, onOpenSettings = {},
    )

    @Composable
    private fun SettingsSample() = SettingsContent(
        state = SettingsState(
            serverUrl = "https://paste.example.com/", deviceName = "Pixel 9",
            deliveryMode = DeliveryMode.AUTOMATIC, batteryExempt = false, appVersion = "0.1.0",
        ),
        onDeliveryMode = {}, onAllowBackground = {}, onSignOut = {}, onBack = {},
    )
}
