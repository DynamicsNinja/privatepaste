package xyz.ficdev.privatepaste.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import xyz.ficdev.privatepaste.PrivatePasteApp

/**
 * After a reboot or an app update, restarts the sync service when a session exists. Both broadcasts are exempt
 * from the Android 12+ background start limit, and Android 15's BOOT_COMPLETED limits do not cover `specialUse`.
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        val app = context.applicationContext as? PrivatePasteApp ?: return
        if (!app.graph.isSignedIn) return
        SyncService.start(context)
    }
}
