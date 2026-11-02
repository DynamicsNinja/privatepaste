package xyz.ficdev.privatepaste.send

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.R
import java.io.FileNotFoundException
import kotlin.coroutines.cancellation.CancellationException

/**
 * Share-sheet target. The shared bytes are read here, while the URI grant lasts; the upload then runs in the app's
 * [SenderProvider.sendScope] so it outlives this activity, which finishes right after the hand-off.
 *
 * Routing: one image stream is an image clip, text is a text clip, and anything else (one other stream, or
 * `SEND_MULTIPLE` of any types) is one file clip.
 */
class ShareActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val provider = application as? SenderProvider
        val sender = provider?.clipSender
        if (provider == null || sender == null) {
            showToast(applicationContext, getString(R.string.sign_in_first))
            finish()
            return
        }
        // Styled text arrives as a Spanned; a Chrome selection share comes wrapped in quotes plus a highlight link.
        val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()?.let(SharedText::selectionOnly)
        val mime = intent.type.orEmpty()
        val multiple = intent.action == Intent.ACTION_SEND_MULTIPLE
        val stream = if (multiple) null else streamExtra()
        lifecycleScope.launch {
            val send: suspend () -> SendResult = when {
                multiple -> prepareFiles(sender, streamsExtra())
                mime.startsWith("image/") -> prepareImage(sender, stream, mime)
                stream != null -> prepareFiles(sender, listOf(stream))
                !text.isNullOrEmpty() -> ({ sender.sendText(text) })
                else -> ({ SendResult.Rejected(ClipSender.NOTHING_TO_SEND) })
            }
            handOff(applicationContext, provider.sendScope, send)
            finish()
        }
    }

    private suspend fun prepareImage(sender: ClipSender, stream: Uri?, mime: String): suspend () -> SendResult {
        if (stream == null || !stream.isSendable()) return { SendResult.Rejected(ClipSender.UNREADABLE_IMAGE) }
        val resolver = contentResolver
        val read = sender.readImage { resolver.openInputStream(stream) ?: throw FileNotFoundException(stream.toString()) }
        val failure = read.toFailure()
        if (failure != null) return { failure }
        val type = if (mime != "image/*") mime else withContext(Dispatchers.IO) { resolver.getType(stream) } ?: "image/png"
        val bytes = (read as ImageRead.Bytes).bytes
        return { sender.sendImageBytes(bytes, type) }
    }

    /** Copies every stream now (the grant ends with this activity); the upload is left for the send scope. */
    private suspend fun prepareFiles(sender: ClipSender, streams: List<Uri>): suspend () -> SendResult {
        if (streams.any { !it.isSendable() }) return { SendResult.Rejected(ClipSender.UNREADABLE_FILES) }
        val resolver = contentResolver
        val sources = withContext(Dispatchers.IO) {
            streams.map { uri ->
                SharedFile(displayName(uri), typeOf(uri)) { resolver.openInputStream(uri) ?: throw FileNotFoundException(uri.toString()) }
            }
        }
        return when (val read = sender.readFiles(sources)) {
            is FilesRead.Failed -> ({ read.result })
            is FilesRead.Ready -> ({ sender.sendFiles(read) })
        }
    }

    /** `OpenableColumns.DISPLAY_NAME`, else the URI's last path segment, else "file". */
    private fun displayName(uri: Uri): String {
        val shown = try {
            contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
                if (c.moveToFirst() && c.columnCount > 0 && !c.isNull(0)) c.getString(0) else null
            }
        } catch (e: Exception) {
            null   // a provider that can't answer: fall back to the URI
        }
        return shown?.takeIf { it.isNotBlank() } ?: uri.lastPathSegment?.takeIf { it.isNotBlank() } ?: "file"
    }

    private fun typeOf(uri: Uri): String? = try {
        contentResolver.getType(uri)?.takeIf { it.isNotBlank() && it != "*/*" }
    } catch (e: Exception) {
        null
    }

    @Suppress("DEPRECATION")
    private fun streamsExtra(): List<Uri> =
        if (Build.VERSION.SDK_INT >= 33) intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
        else intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM).orEmpty()

    @Suppress("DEPRECATION")
    private fun streamExtra(): Uri? =
        if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
        else intent.getParcelableExtra(Intent.EXTRA_STREAM) as? Uri
}

/** Runs [block] in [scope] (it outlives the calling activity) and toasts its result with the application context. */
internal fun handOff(app: Context, scope: CoroutineScope, block: suspend () -> SendResult) {
    scope.launch {
        val message = try {
            block().message
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            "Couldn't send the clip"
        }
        withContext(Dispatchers.Main) { showToast(app, message) }
    }
}

internal fun showToast(app: Context, message: String) = Toast.makeText(app, message, Toast.LENGTH_SHORT).show()
