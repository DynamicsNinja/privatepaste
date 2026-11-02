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
import xyz.ficdev.privatepaste.core.model.ClipFileInfo
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
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.sync.VaultState
import xyz.ficdev.privatepaste.ui.settings.AddDeviceContent
import xyz.ficdev.privatepaste.ui.settings.AddDeviceState
import xyz.ficdev.privatepaste.ui.unlock.UnlockContent
import xyz.ficdev.privatepaste.ui.unlock.UnlockState
import java.time.Duration
import java.io.File
import java.time.Clock
import java.time.Instant
import java.time.ZoneId
import java.util.Locale

/**
 * Saves PNGs of the screens (plus a close-up of the history top bar), dark and light, into the directory named by PP_SCREENSHOT_DIR
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
    @Test fun unlockDark() = shootOne("unlock", dark = true) { UnlockSample(UnlockState(vault = VaultState.LOCKED, passphrase = "correct horse")) }
    @Test fun unlockLight() = shootOne("unlock", dark = false) { UnlockSample(UnlockState(vault = VaultState.LOCKED, passphrase = "correct horse")) }
    @Test fun unlockResetDark() = shootOne("unlock-reset", dark = true) { UnlockSample(resetWrong) }
    @Test fun unlockResetLight() = shootOne("unlock-reset", dark = false) { UnlockSample(resetWrong) }
    @Test fun setupDark() = shootOne("setup", dark = true) { UnlockSample(setup) }
    @Test fun setupLight() = shootOne("setup", dark = false) { UnlockSample(setup) }
    @Test fun waitingDark() = shootOne("waiting", dark = true) { UnlockSample(UnlockState(vault = VaultState.WAITING_FOR_OWNER)) }
    @Test fun waitingLight() = shootOne("waiting", dark = false) { UnlockSample(UnlockState(vault = VaultState.WAITING_FOR_OWNER)) }
    @Test fun addDeviceDark() = shootOne("add-device", dark = true) { AddDeviceSample() }
    @Test fun addDeviceLight() = shootOne("add-device", dark = false) { AddDeviceSample() }
    @Test fun settingsOwnerDark() = shootOne("settings-owner", dark = true) { SettingsSample(owner = true) }
    @Test fun settingsOwnerLight() = shootOne("settings-owner", dark = false) { SettingsSample(owner = true) }
    @Test fun historyUnknownDark() = shootOne("history-unknown", dark = true) { HistorySample(unknown) }
    @Test fun historyUnknownLight() = shootOne("history-unknown", dark = false) { HistorySample(unknown) }
    @Test fun searchOlderDark() = shootOne("history-search-older", dark = true) { HistorySample(searchOlder) }
    @Test fun searchOlderLight() = shootOne("history-search-older", dark = false) { HistorySample(searchOlder) }

    @Test fun historyFilesDark() = shootOne("history-files", dark = true) { HistorySample(fileHistory) }
    @Test fun historyFilesLight() = shootOne("history-files", dark = false) { HistorySample(fileHistory) }

    @Test fun historyFilterDark() = shootOne("history-filter", dark = true) { HistorySample(filesFilter) }
    @Test fun historyFilterLight() = shootOne("history-filter", dark = false) { HistorySample(filesFilter) }

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
    private fun SettingsSample(owner: Boolean = false) = SettingsContent(
        state = SettingsState(
            serverUrl = "https://paste.example.com/", deviceName = "Pixel 9",
            deliveryMode = DeliveryMode.AUTOMATIC, batteryExempt = false, appVersion = "0.1.0", isOwner = owner,
            passphraseChanged = owner,
        ),
        onDeliveryMode = {}, onAllowBackground = {}, onSignOut = {}, onBack = {},
    )

    private val resetWrong = UnlockState(
        vault = VaultState.LOCKED, keyWasReset = true, passphrase = "the old one",
        error = UiText.Res(R.string.vault_wrong_passphrase),
    )

    private val setup = UnlockState(
        vault = VaultState.NEEDS_OWNER_SETUP, passphrase = "a short sentence works", repeat = "a short sentence work",
        error = UiText.Res(R.string.vault_mismatch),
    )

    @Composable
    private fun UnlockSample(state: UnlockState) = UnlockContent(
        state = state,
        onPassphraseChange = {}, onRepeatChange = {}, onUnlock = {}, onSetUp = {}, onScan = {}, onRetry = {}, onSignOut = {},
    )

    @Composable
    private fun AddDeviceSample() = AddDeviceContent(
        state = AddDeviceState(
            loading = false,
            qrText = "privatepaste:handover?id=2f4f7a8e-6b7a-4f3c-9e55-2d2b1c0e9a11&k=q83vEjRWeJCrze8SNFZ4kKvN7xI0VniQq83vEjRWeJA&kid=0123456789abcdef",
            expiresAt = now.plusSeconds(581),
        ),
        left = Duration.ofSeconds(581),
        onNewCode = {}, onBack = {},
    )

    private val unknown = history.copy(
        items = listOf(
            history.items[0],
            clip("u", ClipKind.UNKNOWN, null, 3, "Workstation", "windows"),
            history.items[1],
        ),
    )

    private val fileHistory = history.copy(
        items = listOf(
            clip("f1", ClipKind.FILE, null, 1, "Workstation", "windows")
                .copy(files = listOf(ClipFileInfo("report.pdf", 3_565_158, "application/pdf"), ClipFileInfo("a.png", 100, "image/png"), ClipFileInfo("b.csv", 9, "text/csv"))),
            clip("f2", ClipKind.FILE, null, 4, "Pixel 9", "android")
                .copy(files = listOf(ClipFileInfo("notes.txt", 12_288, "text/plain"))),
            history.items[0],
            history.items[1],
        ),
    )

    private val filesFilter = fileHistory.copy(
        filter = ClipKind.FILE,
        items = fileHistory.items.filter { it.kind == ClipKind.FILE },
    )

    private val searchOlder = HistoryState(query = "invoice", canSearchOlder = true, syncState = SyncState.CONNECTED)
}
