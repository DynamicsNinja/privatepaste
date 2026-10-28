package xyz.ficdev.privatepaste.service

import android.os.Bundle
import android.util.Log
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import xyz.ficdev.privatepaste.PrivatePasteApp
import xyz.ficdev.privatepaste.R

/**
 * Transparent, no-history target of the clip notification's Copy action. It is in the foreground, so the clipboard
 * write is always allowed. Copies the clip from the local cache (the image from the kept file, else fetched), toasts,
 * cancels the clip notification and finishes, on every path.
 */
class CopyActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Notifications.cancelClip(this)
        val id = intent.getStringExtra(EXTRA_CLIP_ID)
        val graph = (application as? PrivatePasteApp)?.graph
        lifecycleScope.launch {
            try {
                val ok = try {
                    id != null && graph != null && graph.copyClip(id)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    Log.w(TAG, "Copy failed (${e.javaClass.simpleName})")
                    false
                }
                Toast.makeText(applicationContext, if (ok) R.string.copied else R.string.copy_failed, Toast.LENGTH_SHORT).show()
            } finally {
                Notifications.cancelClip(applicationContext)
                finish()
            }
        }
    }

    companion object {
        const val EXTRA_CLIP_ID = "xyz.ficdev.privatepaste.extra.CLIP_ID"
        private const val TAG = "CopyActivity"
    }
}
