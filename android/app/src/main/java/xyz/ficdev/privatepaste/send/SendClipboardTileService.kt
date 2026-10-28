package xyz.ficdev.privatepaste.send

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService

/** "Send clipboard" Quick Settings tile: collapses the shade and opens [ClipboardSendActivity] so it has focus. */
class SendClipboardTileService : TileService() {

    override fun onStartListening() {
        qsTile?.let {
            it.state = Tile.STATE_INACTIVE
            it.updateTile()
        }
    }

    override fun onClick() {
        if (isLocked) unlockAndRun { launch() } else launch()
    }

    @SuppressLint("StartActivityAndCollapseDeprecated") // the Intent overload is only used below API 34
    @Suppress("DEPRECATION")
    private fun launch() {
        val intent = Intent(this, ClipboardSendActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            val pending = PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            startActivityAndCollapse(pending)
        } else {
            startActivityAndCollapse(intent)
        }
    }
}
