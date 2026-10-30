package xyz.ficdev.privatepaste.ui

import android.graphics.Bitmap
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.test.captureToImage
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onRoot
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.ui.history.HistoryContent
import xyz.ficdev.privatepaste.ui.history.HistoryState
import xyz.ficdev.privatepaste.ui.theme.PrivatePasteTheme
import java.io.File
import java.time.Clock
import java.time.Instant
import java.time.ZoneId
import java.util.Locale

/**
 * The README hero's phone screen: the history with the demo clips from scripts/readme/demo.json
 * (see scripts/readme/README.md). Runs only when PP_README_DEMO names that file and PP_SCREENSHOT_DIR is set;
 * saves readme-history-{dark,light}.png. Image clips are left out: there is no thumbnail to load here.
 */
@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(qualifiers = "w411dp-h891dp-xxhdpi")
class ReadmeScreenshotTest {

    @get:Rule val compose = createComposeRule()

    @Serializable
    private data class DemoClip(
        val device: String, val platform: String?, val kind: ClipKind, val text: String? = null, val minutesAgo: Long, val pinned: Boolean = false,
        val user: String? = null,
    )

    @Serializable
    private data class Demo(val clips: List<DemoClip>)

    private val demoFile = System.getenv("PP_README_DEMO")?.takeIf { it.isNotBlank() }?.let(::File)
    private val dir = System.getenv("PP_SCREENSHOT_DIR")?.takeIf { it.isNotBlank() }?.let(::File)

    private val now = Instant.parse("2026-10-28T10:00:00Z")
    private val time = RelativeTime(Clock.fixed(now, ZoneId.of("UTC")), ZoneId.of("UTC"), Locale.ENGLISH)

    @Test fun historyDark() = shoot(dark = true)
    @Test fun historyLight() = shoot(dark = false)

    private fun shoot(dark: Boolean) {
        assumeTrue("PP_README_DEMO and PP_SCREENSHOT_DIR not set", demoFile != null && dir != null)
        val demo = Json { ignoreUnknownKeys = true }.decodeFromString<Demo>(demoFile!!.readText())
        val items = demo.clips.filter { it.kind != ClipKind.IMAGE }.mapIndexed { i, c ->
            ClipDto(
                id = "demo-$i", seq = 1000L - i, kind = c.kind, text = c.text, hash = "h-$i", sizeBytes = 10,
                sourceDeviceId = c.device, sourceDeviceName = c.device, sourceDevicePlatform = c.platform, sourceUserName = c.user,
                lastCopiedAt = now.minusSeconds(c.minutesAgo * 60),
                pinnedAt = if (c.pinned) now.minusSeconds(c.minutesAgo * 60 - 60).toString() else null,
            )
        }
        val state = HistoryState(items = items, liveClipId = items.firstOrNull()?.id, syncState = SyncState.CONNECTED)
        compose.setContent {
            PrivatePasteTheme(darkTheme = dark) {
                HistoryContent(
                    state = state, relativeTime = time,
                    onQueryChange = {}, onCopy = {}, onDelete = {}, onNearEnd = {}, onOpenSettings = {},
                )
            }
        }
        compose.waitForIdle()
        val bitmap = compose.onRoot().captureToImage().asAndroidBitmap()
        dir!!.mkdirs()
        File(dir, "readme-history-${if (dark) "dark" else "light"}.png").outputStream().use {
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, it)
        }
    }
}
