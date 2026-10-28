package xyz.ficdev.privatepaste.service

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Nudges the sync engine when the default network changes: a switch to another network, or the current one gaining
 * validated internet. Bursts are debounced to one nudge [debounceMs] after the last event. Capability updates of a
 * network that already had validated internet (signal strength, bandwidth) are ignored, since a nudge drops the
 * current link. Callbacks arrive on a ConnectivityManager thread; [nudge] runs in [scope].
 */
class NetworkWatcher(
    context: Context,
    private val scope: CoroutineScope,
    private val debounceMs: Long = DEBOUNCE_MS,
    private val nudge: () -> Unit,
) {
    private val cm = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    private val lock = Any()
    private var current: Network? = null      // guarded by lock
    private var validated = false             // guarded by lock
    private var pending: Job? = null          // guarded by lock
    private var registered = false            // guarded by lock

    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = synchronized(lock) {
            if (network == current) return@synchronized
            current = network
            validated = false
            schedule()
        }

        override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) = synchronized(lock) {
            val ok = caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) &&
                caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
            val changed = network != current || (ok && !validated)
            current = network
            validated = ok
            if (changed) schedule()
        }

        override fun onLost(network: Network) = synchronized(lock) {
            if (network == current) {
                current = null
                validated = false
            }
        }
    }

    /** Registers the default-network callback. The network in use now is the baseline, not a change. */
    fun start() = synchronized(lock) {
        if (registered || cm == null) return@synchronized
        current = cm.activeNetwork
        validated = current?.let { cm.getNetworkCapabilities(it) }
            ?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) == true
        try {
            cm.registerDefaultNetworkCallback(callback)
            registered = true
        } catch (e: RuntimeException) {
            // SecurityException, or TooManyRequestsException when the app holds too many callbacks.
            Log.w(TAG, "Cannot watch the network (${e.javaClass.simpleName})")
        }
    }

    fun stop() = synchronized(lock) {
        pending?.cancel()
        pending = null
        if (!registered) return@synchronized
        registered = false
        try {
            cm?.unregisterNetworkCallback(callback)
        } catch (e: IllegalArgumentException) {
            Log.w(TAG, "Network callback was not registered")
        }
    }

    // Caller holds lock.
    private fun schedule() {
        if (!registered) return
        pending?.cancel()
        pending = scope.launch {
            delay(debounceMs)
            nudge()
        }
    }

    private companion object {
        const val TAG = "NetworkWatcher"
        const val DEBOUNCE_MS = 2_000L
    }
}
