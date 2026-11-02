package xyz.ficdev.privatepaste.clipboard

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import androidx.core.content.FileProvider
import java.io.File

/**
 * Puts clips from other devices on this phone's clipboard, labelled "PrivatePaste". Images go through this app's
 * FileProvider as `content://xyz.ficdev.privatepaste.files/clipboard/...` with MIME image/png, a file clip's files as
 * `.../files/<clipId>/<name>`. Never marks a clip
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

    /** A file clip's opened files (ClipFileStore), one clip item per file, in order. */
    fun writeFiles(files: List<File>) {
        require(files.isNotEmpty()) { "No files" }
        val resolver = context.contentResolver
        // Each item asks the provider for its type, so the description lists every file's MIME type.
        val clip = ClipData.newUri(resolver, LABEL, contentUri(files[0]))
        for (f in files.drop(1)) clip.addItem(resolver, ClipData.Item(contentUri(f)))
        clipboard.setPrimaryClip(clip)
    }

    /** The content URI this app's FileProvider serves [file] under (it must be in a file_paths.xml folder). */
    fun contentUri(file: File): Uri = uriFor(context, file)

    companion object {
        const val LABEL = "PrivatePaste"
        const val AUTHORITY = "xyz.ficdev.privatepaste.files"
    }
}
