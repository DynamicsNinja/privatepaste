package xyz.ficdev.privatepaste.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.SystemClock
import android.util.Log
import androidx.core.content.ContextCompat

/**
 * Calls [onWake] when the screen comes on or the user unlocks. Coroutine delays do not advance in deep sleep, so
 * after a wake the engine may sit out a backoff or a pong timeout; [onWake] lets it check the link at once.
 * A burst (screen on, then unlock) collapses to one call per [minIntervalMs]. Runtime-registered: these broadcasts
 * never reach a manifest receiver. Main thread.
 */
class WakeWatcher(
    context: Context,
    private val minIntervalMs: Long = MIN_INTERVAL_MS,
    private val now: () -> Long = SystemClock::elapsedRealtime,
    private val onWake: () -> Unit,
) {
    private val context = context.applicationContext
    private var lastAt: Long? = null
    private var registered = false

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action != Intent.ACTION_SCREEN_ON && intent.action != Intent.ACTION_USER_PRESENT) return
            val t = now()
            val last = lastAt
            if (last != null && t - last < minIntervalMs) return
            lastAt = t
            onWake()
        }
    }

    fun start() {
        if (registered) return
        val filter = IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_ON)
            addAction(Intent.ACTION_USER_PRESENT)
        }
        // System broadcasts still arrive at a not-exported receiver.
        ContextCompat.registerReceiver(context, receiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
        registered = true
    }

    fun stop() {
        if (!registered) return
        registered = false
        try {
            context.unregisterReceiver(receiver)
        } catch (e: IllegalArgumentException) {
            Log.w(TAG, "Wake receiver was not registered")
        }
    }

    private companion object {
        const val TAG = "WakeWatcher"
        const val MIN_INTERVAL_MS = 2_000L
    }
}
