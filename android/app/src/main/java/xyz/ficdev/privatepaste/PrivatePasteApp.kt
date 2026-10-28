package xyz.ficdev.privatepaste

import android.app.Activity
import android.app.Application
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import xyz.ficdev.privatepaste.clipboard.ClipImageFiles
import xyz.ficdev.privatepaste.clipboard.ClipboardWriter
import xyz.ficdev.privatepaste.send.ClipSender
import xyz.ficdev.privatepaste.send.SenderProvider
import xyz.ficdev.privatepaste.service.Notifications
import xyz.ficdev.privatepaste.service.SyncService
import xyz.ficdev.privatepaste.storage.KeyProvider
import xyz.ficdev.privatepaste.storage.KeystoreKeyProvider
import xyz.ficdev.privatepaste.ui.UiDeps
import xyz.ficdev.privatepaste.ui.UiDepsProvider

/**
 * The Application: owns the [AppGraph] (built on first use, so components that never need it stay cheap) and the
 * app-lifetime [appScope]. While an activity of this app is started and a session exists, it makes sure the sync
 * service runs (an app start in the foreground is always allowed to start it).
 */
open class PrivatePasteApp : Application(), UiDepsProvider, SenderProvider {

    val appScope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    val graph: AppGraph by lazy { AppGraph(this, keyProvider(), appScope) }

    override val uiDeps: UiDeps get() = graph.uiDeps
    override val clipSender: ClipSender? get() = graph.current.value?.sender
    override val sendScope: CoroutineScope get() = appScope

    private val main = Handler(Looper.getMainLooper())
    private var startedActivities = 0   // main thread only

    /** Test seam: Robolectric has no AndroidKeyStore. */
    internal open fun keyProvider(): KeyProvider = KeystoreKeyProvider()

    /** Test seam: androidx FileProvider cannot map paths on a Windows host. */
    internal open fun clipboardWriter(files: ClipImageFiles): ClipboardWriter = ClipboardWriter(this, files)

    override fun onCreate() {
        super.onCreate()
        Notifications.createChannels(this)
        registerActivityLifecycleCallbacks(object : ActivityLifecycleCallbacks {
            override fun onActivityStarted(activity: Activity) {
                startedActivities++
                // After process death without the service, or a refused background start: opening the app resumes sync.
                if (startedActivities == 1 && graph.isSignedIn) SyncService.start(this@PrivatePasteApp)
            }

            override fun onActivityStopped(activity: Activity) {
                startedActivities--
            }

            override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) = Unit
            override fun onActivityResumed(activity: Activity) = Unit
            override fun onActivityPaused(activity: Activity) = Unit
            override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit
            override fun onActivityDestroyed(activity: Activity) = Unit
        })
    }

    /**
     * Messages from the sender about queued clips ("Couldn't sync a clip after several tries.", the server's
     * refusal): a toast while one of the app's activities shows, else an alert notification. Any thread.
     */
    internal fun notifyUser(message: String) {
        main.post {
            if (startedActivities > 0) {
                Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
            } else {
                Notifications.postAlert(this, message)
            }
        }
    }
}
