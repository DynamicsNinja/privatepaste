package xyz.ficdev.privatepaste.ui

import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.ui.history.HistoryContent
import xyz.ficdev.privatepaste.ui.history.HistoryState
import xyz.ficdev.privatepaste.ui.theme.PrivatePasteTheme
import java.time.Clock
import java.time.Instant
import java.time.ZoneId
import java.util.Locale

@RunWith(RobolectricTestRunner::class)
class HistoryScreenTest {

    @get:Rule val compose = createComposeRule()

    private val now = Instant.parse("2026-10-28T10:00:00Z")
    private val time = RelativeTime(Clock.fixed(now, ZoneId.of("UTC")), ZoneId.of("UTC"), Locale.ENGLISH)

    private val clip = ClipDto(
        id = "a", seq = 1, kind = ClipKind.TEXT, text = "hello from the desk", hash = "h-a", sizeBytes = 19,
        sourceDeviceId = "pc", sourceDeviceName = "Desk", lastCopiedAt = now.minusSeconds(5 * 60),
    )

    private val copied = mutableListOf<String>()
    private val deleted = mutableListOf<String>()

    private fun show(state: HistoryState) = compose.setContent {
        PrivatePasteTheme {
            HistoryContent(
                state = state,
                relativeTime = time,
                onQueryChange = {},
                onCopy = { copied += it.id },
                onDelete = { deleted += it.id },
                onNearEnd = {},
                onOpenSettings = {},
            )
        }
    }

    @Test
    fun emptyStateText() {
        show(HistoryState(syncState = SyncState.CONNECTED))
        compose.onNodeWithText("Nothing here yet. Copy something on your computer and it shows up here.").assertIsDisplayed()
        compose.onNodeWithText("Search history").assertExists()
        compose.onNodeWithText("Connected").assertIsDisplayed()
        compose.onNodeWithContentDescription("Settings").assertIsDisplayed()
    }

    @Test
    fun rowShowsThePlatformIconBeforeTheDevice() {
        show(
            HistoryState(
                items = listOf(
                    clip.copy(sourceDevicePlatform = "android"),
                    clip.copy(id = "b", seq = 2, hash = "h-b", text = "from nowhere", sourceDevicePlatform = null),
                ),
                syncState = SyncState.CONNECTED,
            ),
        )
        compose.onNodeWithContentDescription("Android", useUnmergedTree = true).assertIsDisplayed()
        compose.onNodeWithContentDescription("Unknown device", useUnmergedTree = true).assertIsDisplayed()
    }

    @Test
    fun rowShowsPreviewDeviceAndTime() {
        show(HistoryState(items = listOf(clip), liveClipId = "a", syncState = SyncState.OFFLINE))
        compose.onNodeWithText("hello from the desk").assertIsDisplayed()
        compose.onNodeWithText("Desk · 5 min").assertIsDisplayed()
        compose.onNodeWithText("Offline").assertIsDisplayed()
        compose.onNodeWithContentDescription("Applied on this phone").assertExists()
    }

    @Test
    fun tapCopies() {
        show(HistoryState(items = listOf(clip), syncState = SyncState.CONNECTED))
        compose.onNodeWithText("hello from the desk").performClick()
        compose.waitForIdle()
        assertEquals(listOf("a"), copied)
    }

    @Test
    fun longPressShowsTheDeleteDialog() {
        show(HistoryState(items = listOf(clip), syncState = SyncState.CONNECTING))
        compose.onNodeWithText("Connecting…").assertIsDisplayed()

        compose.onNodeWithText("hello from the desk").performTouchInput { longClick() }
        compose.onNodeWithText("Delete this clip from all your devices?").assertIsDisplayed()
        compose.onNodeWithText("Cancel").performClick()
        compose.onNodeWithText("Delete this clip from all your devices?").assertDoesNotExist()
        assertEquals(emptyList<String>(), deleted)

        compose.onNodeWithText("hello from the desk").performTouchInput { longClick() }
        compose.onNodeWithText("Delete").performClick()
        compose.waitForIdle()
        assertEquals(listOf("a"), deleted)
        assertEquals(emptyList<String>(), copied)
    }
}
