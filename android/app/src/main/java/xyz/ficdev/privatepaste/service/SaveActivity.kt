package xyz.ficdev.privatepaste.service

import android.os.Bundle
import android.util.Log
import android.widget.Toast
import androidx.activity.ComponentActivity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.PrivatePasteApp
import xyz.ficdev.privatepaste.R

/**
 * Transparent, no-history target of a file clip notification's Save action: downloads the files if they aren't here
 * yet, saves them to Downloads, toasts and cancels the clip notification. It finishes at once: the work runs in the
 * app's scope (the toast uses the app's context), so a large download neither keeps it on top nor stops half-way.
 */
class SaveActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Notifications.cancelClip(this)
        val id = intent.getStringExtra(EXTRA_CLIP_ID)
        val app = application as? PrivatePasteApp
        if (app == null) {
            finish()
            return
        }
        app.appScope.launch {
            val ok = try {
                id != null && app.graph.saveClipFiles(id)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                Log.w(TAG, "Save failed (${e.javaClass.simpleName})")
                false
            }
            withContext(Dispatchers.Main) {
                Toast.makeText(app, if (ok) R.string.saved_to_downloads else R.string.save_failed, Toast.LENGTH_SHORT).show()
                Notifications.cancelClip(app)
            }
        }
        finish()   // nothing to show meanwhile: staying on top would block the screen for a large download
    }

    companion object {
        /** The same extra as [CopyActivity.EXTRA_CLIP_ID]: the notification fills both the same way. */
        const val EXTRA_CLIP_ID = CopyActivity.EXTRA_CLIP_ID
        private const val TAG = "SaveActivity"
    }
}
