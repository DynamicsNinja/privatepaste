package xyz.ficdev.privatepaste.files

import android.content.ContentValues
import android.content.Context
import android.os.Environment
import android.provider.MediaStore
import android.util.Log
import xyz.ficdev.privatepaste.core.FileNames
import java.io.File
import java.io.IOException

/** Puts opened files where the user keeps downloads. True when every file was saved. */
fun interface FileSaver {
    fun save(files: List<File>): Boolean
}

/**
 * Saves into the shared Downloads folder through MediaStore (API 29+: no storage permission). A name already there
 * gets " (2)", " (3)" ... before the extension; the MIME type comes from the name's extension, never from the payload.
 * Each entry stays pending until its bytes are written; a failed write removes it. Blocking: call it off the main thread.
 */
class DownloadsSaver(context: Context) : FileSaver {
    private val resolver = context.applicationContext.contentResolver
    private val collection = MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
    private val folder = "${Environment.DIRECTORY_DOWNLOADS}/"

    override fun save(files: List<File>): Boolean = files.all(::saveOne)

    private fun saveOne(file: File): Boolean {
        val name = FileNames.unique(file.name, ::exists)
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.MIME_TYPE, FileNames.mimeFor(name))
            put(MediaStore.MediaColumns.RELATIVE_PATH, folder)
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri = try {
            resolver.insert(collection, values)
        } catch (e: Exception) {
            Log.w(TAG, "Cannot add a download (${e.javaClass.simpleName})")
            null
        } ?: return false
        return try {
            val out = resolver.openOutputStream(uri) ?: throw IOException("No output stream")
            out.use { o -> file.inputStream().use { it.copyTo(o) } }
            resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
            true
        } catch (e: Exception) {
            // IOException, SecurityException, or whatever the media provider throws (a full disk, say).
            Log.w(TAG, "Cannot write a download (${e.javaClass.simpleName})")
            try {
                resolver.delete(uri, null, null)
            } catch (_: Exception) {
            }
            false
        }
    }

    /** Whether Downloads already has [name] (as far as this app can see: its own files, and others' it may read). */
    private fun exists(name: String): Boolean = try {
        resolver.query(
            collection, arrayOf(MediaStore.MediaColumns._ID),
            "${MediaStore.MediaColumns.DISPLAY_NAME}=? AND ${MediaStore.MediaColumns.RELATIVE_PATH}=?", arrayOf(name, folder), null,
        )?.use { it.count > 0 } ?: false
    } catch (e: Exception) {
        false
    }

    private companion object {
        const val TAG = "DownloadsSaver"
    }
}
