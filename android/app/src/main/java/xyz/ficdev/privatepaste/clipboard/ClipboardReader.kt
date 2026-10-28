package xyz.ficdev.privatepaste.clipboard

import android.content.ClipboardManager
import android.content.Context
import android.net.Uri

sealed interface ClipboardContent {
    data class Text(val text: String) : ClipboardContent
    data class Image(val uri: Uri, val mime: String) : ClipboardContent
}

/**
 * Reads the primary clip. Since Android 10 only the focused app (or the default IME) may read the clipboard, so
 * call [read] only while one of this app's activities has window focus (for example from `onWindowFocusChanged`);
 * from the background, or before focus, it returns null.
 */
class ClipboardReader(context: Context) {

    private val context = context.applicationContext
    private val clipboard = this.context.getSystemService(ClipboardManager::class.java)

    /** The first item of the primary clip: its text when it has any (text wins), else an image URI; null otherwise. */
    fun read(): ClipboardContent? {
        val clip = try {
            clipboard.primaryClip
        } catch (_: SecurityException) {
            null
        } ?: return null
        if (clip.itemCount == 0) return null
        val item = clip.getItemAt(0)
        item.text?.toString()?.takeIf { it.isNotEmpty() }?.let { return ClipboardContent.Text(it) }
        val uri = item.uri ?: return null
        val mime = clip.description.filterMimeTypes("image/*")?.firstOrNull()
            ?: context.contentResolver.getType(uri)?.takeIf { it.startsWith("image/") }
            ?: return null
        return ClipboardContent.Image(uri, mime)
    }
}
