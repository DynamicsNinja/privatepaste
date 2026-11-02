package xyz.ficdev.privatepaste.service

import android.content.Context
import android.os.Bundle
import android.util.Log
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.AppGraph
import xyz.ficdev.privatepaste.PrivatePasteApp
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.core.model.ClipKind

/**
 * Transparent, no-history target of the clip notification's Copy action. It is in the foreground, so the clipboard
 * write is always allowed. Copies the clip from the local cache (the image from the kept file, else fetched), toasts,
 * cancels the clip notification and finishes, on every path. A file clip may need a large download first: that runs
 * in the app's scope and the activity finishes at once, so it never stays on top meanwhile (writing the clipboard
 * needs no focus; reading it does).
 */
class CopyActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Notifications.cancelClip(this)
        val id = intent.getStringExtra(EXTRA_CLIP_ID)
        val app = application as? PrivatePasteApp
        val graph = app?.graph
        val isFile = id != null && graph?.cache?.items?.value?.any { it.id == id && it.kind == ClipKind.FILE } == true
        if (app != null && isFile) {
            app.appScope.launch {
                val ok = copy(graph, id)
                withContext(Dispatchers.Main) { done(app, ok) }
            }
            finish()
            return
        }
        lifecycleScope.launch {
            try {
                done(applicationContext, copy(graph, id))
            } finally {
                Notifications.cancelClip(applicationContext)
                finish()
            }
        }
    }

    private suspend fun copy(graph: AppGraph?, id: String?): Boolean = try {
        id != null && graph != null && graph.copyClip(id)
    } catch (e: CancellationException) {
        throw e
    } catch (e: Exception) {
        Log.w(TAG, "Copy failed (${e.javaClass.simpleName})")
        false
    }

    private fun done(context: Context, ok: Boolean) {
        Toast.makeText(context, if (ok) R.string.copied else R.string.copy_failed, Toast.LENGTH_SHORT).show()
        Notifications.cancelClip(context)
    }

    companion object {
        const val EXTRA_CLIP_ID = "xyz.ficdev.privatepaste.extra.CLIP_ID"
        private const val TAG = "CopyActivity"
    }
}
