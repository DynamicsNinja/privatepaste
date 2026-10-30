package xyz.ficdev.privatepaste.service

import android.Manifest
import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import xyz.ficdev.privatepaste.MainActivity
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.sync.SyncState

/**
 * The app's three channels and every notification it posts. Posting needs POST_NOTIFICATIONS on Android 13+;
 * without it nothing is posted (and nothing throws). The foreground service still runs, its notification hidden.
 */
object Notifications {
    const val CHANNEL_CONNECTION = "connection"
    const val CHANNEL_CLIPS = "clips"
    const val CHANNEL_ALERTS = "alerts"

    const val ID_CONNECTION = 1
    const val ID_CLIP = 2
    const val ID_SIGNED_OUT = 3
    const val ID_ALERT = 4
    const val ID_SYNC_PAUSED = 5

    const val SIGNED_OUT = "Signed out. Sign in again to keep syncing."
    const val SYNC_PAUSED = "Sync paused. Open PrivatePaste to resume."

    private const val TAG = "Notifications"
    private const val PREVIEW_CHARS = 100
    private const val ALERT_TIMEOUT_MS = 10_000L
    private const val REQUEST_OPEN = 0
    private const val REQUEST_COPY = 1

    /** Idempotent; call from `Application.onCreate`. */
    fun createChannels(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java) ?: return
        nm.createNotificationChannels(
            listOf(
                NotificationChannel(CHANNEL_CONNECTION, "Connection", NotificationManager.IMPORTANCE_MIN).apply {
                    description = "Shown while PrivatePaste keeps the connection to your server"
                    setShowBadge(false)
                },
                NotificationChannel(CHANNEL_CLIPS, "Clips", NotificationManager.IMPORTANCE_HIGH).apply {
                    description = "A clip from another device, with a Copy button"
                },
                NotificationChannel(CHANNEL_ALERTS, "Alerts", NotificationManager.IMPORTANCE_DEFAULT).apply {
                    description = "Sign-out and sync problems"
                },
            ),
        )
    }

    fun connectionText(state: SyncState): String = when (state) {
        SyncState.CONNECTED -> "PrivatePaste connected"
        SyncState.CONNECTING -> "Connecting…"
        SyncState.OFFLINE -> "Offline, retrying"
        SyncState.SIGNED_OUT -> "Signed out"
    }

    /** The foreground service's ongoing notification; tapping it opens the history. */
    fun connection(context: Context, state: SyncState): Notification =
        NotificationCompat.Builder(context, CHANNEL_CONNECTION)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(connectionText(state))
            .setContentIntent(openApp(context))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_MIN)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()

    fun updateConnection(context: Context, state: SyncState) {
        post(context, ID_CONNECTION, connection(context, state))
    }

    /**
     * Posts (or replaces: one id for all) the heads-up for [clip] with its Copy action. False when notifications are
     * not allowed, so the engine does not record the clip as applied.
     */
    fun postClip(context: Context, clip: ClipDto): Boolean {
        val device = clip.sourceDeviceName ?: "another device"
        val title = clip.sourceUserName?.takeIf { it.isNotBlank() }
            ?.let { context.getString(R.string.notification_new_clip_from_person, it, device) }
            ?: "New clip from $device"
        val body = when (clip.kind) {
            ClipKind.IMAGE -> if (clip.width != null && clip.height != null) "Image ${clip.width}×${clip.height}" else "Image"
            ClipKind.TEXT, ClipKind.LINK -> preview(clip.text.orEmpty())
        }
        val copy = Intent(context, CopyActivity::class.java)
            .putExtra(CopyActivity.EXTRA_CLIP_ID, clip.id)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        val copyIntent = PendingIntent.getActivity(
            context, REQUEST_COPY, copy, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        // Clips can be passwords: the lock screen shows only that a clip arrived.
        val public = NotificationCompat.Builder(context, CHANNEL_CLIPS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .build()
        val n = NotificationCompat.Builder(context, CHANNEL_CLIPS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(body)
            .setContentIntent(openApp(context))
            .addAction(0, "Copy", copyIntent)
            .setAutoCancel(true)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(public)
            .build()
        return post(context, ID_CLIP, n, channel = CHANNEL_CLIPS)
    }

    fun cancelClip(context: Context) = NotificationManagerCompat.from(context).cancel(ID_CLIP)

    /** "Signed out. Sign in again to keep syncing." Stays until dismissed or the app opens. */
    fun postSignedOut(context: Context) {
        post(context, ID_SIGNED_OUT, alertBuilder(context, SIGNED_OUT).build())
    }

    fun cancelSignedOut(context: Context) = NotificationManagerCompat.from(context).cancel(ID_SIGNED_OUT)

    /**
     * "Sync paused. Open PrivatePaste to resume." The system refused the foreground start; opening the app starts the
     * service again and cancels this. One fixed id, alerting once: a repeated refusal never posts a second one.
     */
    fun postSyncPaused(context: Context) {
        post(context, ID_SYNC_PAUSED, alertBuilder(context, SYNC_PAUSED).setOnlyAlertOnce(true).build())
    }

    fun cancelSyncPaused(context: Context) = NotificationManagerCompat.from(context).cancel(ID_SYNC_PAUSED)

    /** A transient message (size limit, a queued clip given up on) while no activity shows a toast: gone after 10 s. */
    fun postAlert(context: Context, message: String) {
        post(context, ID_ALERT, alertBuilder(context, message).setTimeoutAfter(ALERT_TIMEOUT_MS).build())
    }

    private fun alertBuilder(context: Context, message: String) =
        NotificationCompat.Builder(context, CHANNEL_ALERTS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(context.getString(R.string.app_name))
            .setContentText(message)
            .setStyle(NotificationCompat.BigTextStyle().bigText(message))
            .setContentIntent(openApp(context))
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_STATUS)

    private fun openApp(context: Context): PendingIntent = PendingIntent.getActivity(
        context, REQUEST_OPEN,
        Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    /** At most [PREVIEW_CHARS] chars, never splitting a surrogate pair. */
    internal fun preview(text: String): String {
        if (text.length <= PREVIEW_CHARS) return text
        val end = if (Character.isHighSurrogate(text[PREVIEW_CHARS - 1])) PREVIEW_CHARS - 1 else PREVIEW_CHARS
        return text.substring(0, end)
    }

    /** Whether a post would be shown: the runtime permission (13+), the app switch and, if given, the channel. */
    private fun canPost(context: Context, channel: String?): Boolean {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) return false
        val nm = NotificationManagerCompat.from(context)
        if (!nm.areNotificationsEnabled()) return false
        if (channel != null && nm.getNotificationChannel(channel)?.importance == NotificationManager.IMPORTANCE_NONE) return false
        return true
    }

    @SuppressLint("MissingPermission") // checked by canPost
    private fun post(context: Context, id: Int, n: Notification, channel: String? = null): Boolean {
        if (!canPost(context, channel)) return false
        return try {
            NotificationManagerCompat.from(context).notify(id, n)
            true
        } catch (e: SecurityException) {
            Log.w(TAG, "Cannot post a notification (${e.javaClass.simpleName})")
            false
        }
    }
}
