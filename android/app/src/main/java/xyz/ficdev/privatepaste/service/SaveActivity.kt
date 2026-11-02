package xyz.ficdev.privatepaste.service

import android.os.Bundle
import android.util.Log
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.PrivatePasteApp
import xyz.ficdev.privatepaste.R

/**
 * Transparent, no-history target of a file clip notification's Save action: downloads the files if they aren't here
 * yet, saves them to Downloads, toasts, cancels the clip notification and finishes, on every path. The work runs in
 * the app's scope, so leaving the activity (it has no history) doesn't abort a large download half-way.
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
        val work = app.appScope.launch {
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
        lifecycleScope.launch {
            work.join()
            finish()
        }
    }

    companion object {
        /** The same extra as [CopyActivity.EXTRA_CLIP_ID]: the notification fills both the same way. */
        const val EXTRA_CLIP_ID = CopyActivity.EXTRA_CLIP_ID
        private const val TAG = "SaveActivity"
    }
}
