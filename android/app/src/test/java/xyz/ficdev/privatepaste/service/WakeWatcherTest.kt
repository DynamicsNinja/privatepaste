package xyz.ficdev.privatepaste.service

import android.content.Context
import android.content.Intent
import android.os.Looper
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf

@RunWith(RobolectricTestRunner::class)
class WakeWatcherTest {

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private var clock = 10_000L

    private fun send(action: String) {
        context.sendBroadcast(Intent(action))
        shadowOf(Looper.getMainLooper()).idle()
    }

    @Test
    fun screenOnAndUnlockCollapseToOneCallPerTwoSeconds() {
        var wakes = 0
        val watcher = WakeWatcher(context, now = { clock }) { wakes++ }
        watcher.start()

        send(Intent.ACTION_SCREEN_ON)
        assertEquals(1, wakes)
        clock += 1_500
        send(Intent.ACTION_USER_PRESENT)   // the unlock right after: same burst
        assertEquals(1, wakes)
        clock += 500
        send(Intent.ACTION_USER_PRESENT)
        assertEquals(2, wakes)

        watcher.stop()
        clock += 10_000
        send(Intent.ACTION_SCREEN_ON)
        assertEquals("unregistered", 2, wakes)
    }
}
