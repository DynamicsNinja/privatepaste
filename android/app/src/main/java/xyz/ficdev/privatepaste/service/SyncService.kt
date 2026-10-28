package xyz.ficdev.privatepaste.service

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import xyz.ficdev.privatepaste.PrivatePasteApp
import xyz.ficdev.privatepaste.sync.SyncState

/**
 * The foreground service that owns the sync engine's lifecycle while signed in: it starts the current session's
 * engine on every start command and stops it in [onDestroy], so the engine never runs without it. It shows the
 * connection state, nudges the engine on network changes, and stops itself once the session is gone (sign-out or
 * revoke). Sticky: after process death the system restarts it, and it picks the session up from storage.
 */
class SyncService : Service() {

    private var scope: CoroutineScope? = null
    private var watcher: NetworkWatcher? = null

    /** The newest start id delivered to [onStartCommand]. Stopping by it never drops a start still pending. */
    private var lastStartId = 0

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        lastStartId = startId
        val app = application as? PrivatePasteApp
        val graph = app?.graph
        // startForeground first, every time: each startForegroundService call must be answered, also when stopping.
        val shown = graph?.connectionStateNow()?.takeIf { it != SyncState.SIGNED_OUT } ?: SyncState.CONNECTING
        if (!goForeground(shown)) {
            stopSelf(startId)
            return START_NOT_STICKY
        }
        if (graph == null || !graph.startEngine()) {
            // By id: a newer start still pending keeps the service, gets its startForeground, and stops then.
            stopSelf(startId)
            return START_NOT_STICKY
        }
        if (scope == null) watch(app)
        return START_STICKY
    }

    private fun watch(app: PrivatePasteApp) {
        val graph = app.graph
        val s = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        scope = s
        s.launch {
            graph.connectionStates.collect { Notifications.updateConnection(this@SyncService, it) }
        }
        s.launch {
            graph.current.collect { session ->
                // Signed out or revoked: stop, but only up to the last delivered start (see lastStartId).
                if (session == null) stopSelfResult(lastStartId) else graph.startEngine()   // a new session runs here too
            }
        }
        watcher = NetworkWatcher(this, s) { graph.current.value?.engine?.nudge() }.also { it.start() }
    }

    private fun goForeground(state: SyncState): Boolean = try {
        val n = Notifications.connection(this, state)
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(Notifications.ID_CONNECTION, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(Notifications.ID_CONNECTION, n)
        }
        true
    } catch (e: IllegalStateException) {
        // ForegroundServiceStartNotAllowedException (Android 12+, started from the background without an exemption).
        Log.w(TAG, "Cannot run in the foreground (${e.javaClass.simpleName})")
        false
    } catch (e: SecurityException) {
        Log.w(TAG, "Cannot run in the foreground (${e.javaClass.simpleName})")
        false
    }

    /** The user swiped the app away: keep syncing (the point of the service). */
    override fun onTaskRemoved(rootIntent: Intent?) = Unit

    override fun onDestroy() {
        watcher?.stop()
        watcher = null
        scope?.cancel()
        scope = null
        // The state updates re-post the same id: make sure no "connected" notification outlives the service.
        stopForeground(STOP_FOREGROUND_REMOVE)
        NotificationManagerCompat.from(this).cancel(Notifications.ID_CONNECTION)
        (application as? PrivatePasteApp)?.graph?.stopEngine()
        super.onDestroy()
    }

    companion object {
        const val ACTION_START = "xyz.ficdev.privatepaste.action.START_SYNC"
        private const val TAG = "SyncService"

        /**
         * Starts (or re-confirms) the service. Idempotent. From the background on Android 12+ the system may refuse
         * (outside the boot and update broadcasts): that is logged, never thrown; the next app launch starts it.
         * There is no `stop`: the service stops itself, by start id, once the session is gone. A `stopService` could
         * drop a start still pending and crash the app ("did not then call startForeground").
         */
        fun start(context: Context) {
            val intent = Intent(context, SyncService::class.java).setAction(ACTION_START)
            try {
                ContextCompat.startForegroundService(context, intent)
            } catch (e: IllegalStateException) {
                // ForegroundServiceStartNotAllowedException / BackgroundServiceStartNotAllowedException.
                Log.w(TAG, "Cannot start the sync service now (${e.javaClass.simpleName})")
            } catch (e: SecurityException) {
                Log.w(TAG, "Cannot start the sync service (${e.javaClass.simpleName})")
            }
        }
    }
}
