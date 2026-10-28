package xyz.ficdev.privatepaste.service

import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestCoroutineScheduler
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.shadows.ShadowNetwork
import org.robolectric.shadows.ShadowNetworkCapabilities

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
class NetworkWatcherTest {

    private val context = ApplicationProvider.getApplicationContext<android.content.Context>()
    private val cm = context.getSystemService(ConnectivityManager::class.java)
    private val scheduler = TestCoroutineScheduler()
    private val scope = CoroutineScope(StandardTestDispatcher(scheduler))

    private fun validated(): NetworkCapabilities = ShadowNetworkCapabilities.newInstance().also {
        shadowOf(it).addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        shadowOf(it).addCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
    }

    private fun unvalidated(): NetworkCapabilities = ShadowNetworkCapabilities.newInstance().also {
        shadowOf(it).addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
    }

    @Test
    fun networkCallbackNudgesEngineDebounced() {
        var nudges = 0
        val watcher = NetworkWatcher(context, scope) { nudges++ }
        watcher.start()
        val callback = shadowOf(cm).networkCallbacks.single()

        // A switch to another network: onAvailable, then its capabilities settle. One nudge, 2 s after the last event.
        val wifi = ShadowNetwork.newInstance(4242)
        callback.onAvailable(wifi)
        scheduler.advanceTimeBy(1_000)
        callback.onCapabilitiesChanged(wifi, unvalidated())
        scheduler.advanceTimeBy(500)
        callback.onCapabilitiesChanged(wifi, validated())
        scheduler.advanceTimeBy(1_999)
        scheduler.runCurrent()
        assertEquals("debounced", 0, nudges)
        scheduler.advanceTimeBy(2)
        scheduler.runCurrent()
        assertEquals(1, nudges)

        // Signal-strength style updates of the same validated network do not drop a working link.
        callback.onCapabilitiesChanged(wifi, validated())
        callback.onAvailable(wifi)
        scheduler.advanceTimeBy(5_000)
        scheduler.runCurrent()
        assertEquals(1, nudges)

        // Lost, then back: another nudge.
        callback.onLost(wifi)
        callback.onAvailable(ShadowNetwork.newInstance(4243))
        scheduler.advanceTimeBy(2_001)
        scheduler.runCurrent()
        assertEquals(2, nudges)

        // Teardown unregisters and drops a pending nudge.
        callback.onAvailable(ShadowNetwork.newInstance(4244))
        watcher.stop()
        scheduler.advanceTimeBy(5_000)
        scheduler.runCurrent()
        assertEquals(2, nudges)
        assertTrue(shadowOf(cm).networkCallbacks.isEmpty())
    }
}
