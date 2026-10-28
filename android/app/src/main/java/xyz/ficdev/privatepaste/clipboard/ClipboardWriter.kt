package xyz.ficdev.privatepaste.clipboard

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import androidx.core.content.FileProvider
import java.io.File

/**
 * Puts clips from other devices on this phone's clipboard, labelled "PrivatePaste". Images go through this app's
 * FileProvider as `content://xyz.ficdev.privatepaste.files/clipboard/...` with MIME image/png. Never marks a clip
 * as sensitive (owner decision: passwords are treated like any other clip).
 */
class ClipboardWriter internal constructor(
    context: Context,
    private val files: ClipImageFiles,
    /** Maps a stored file to its content URI. Unit tests on a Windows host swap it: FileProvider assumes '/'. */
    private val uriFor: (Context, File) -> Uri,
) {
    constructor(context: Context, files: ClipImageFiles) :
        this(context, files, { c, f -> FileProvider.getUriForFile(c, AUTHORITY, f) })

    private val context = context.applicationContext
    private val clipboard = this.context.getSystemService(ClipboardManager::class.java)

    fun writeText(text: String) {
        clipboard.setPrimaryClip(ClipData.newPlainText(LABEL, text))
    }

    /** @throws java.io.IOException when the image file cannot be written. */
    fun writeImage(clipId: String, png: ByteArray) {
        val file = files.store(clipId, png)
        val uri = uriFor(context, file)
        // newUri asks the provider for the type: FileProvider answers image/png from the .png extension.
        clipboard.setPrimaryClip(ClipData.newUri(context.contentResolver, LABEL, uri))
    }

    companion object {
        const val LABEL = "PrivatePaste"
        const val AUTHORITY = "xyz.ficdev.privatepaste.files"
    }
}
