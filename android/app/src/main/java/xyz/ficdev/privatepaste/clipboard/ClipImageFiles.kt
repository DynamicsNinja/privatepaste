package xyz.ficdev.privatepaste.clipboard

import java.io.File

/**
 * Plain PNG files under `cacheDir/clipboard`, served to other apps by the FileProvider while they are on the
 * clipboard. Only the newest [KEEP] files are kept: an older clipboard URI stops resolving once it rotates out.
 * File names are `<order>_<clipId>.png`, where order is a monotonic counter, so "newest" does not depend on the
 * file system's timestamp resolution.
 */
class ClipImageFiles(private val dir: File) {

    private val lock = Any()
    private var lastOrder = -1L

    /** Writes [png] for [clipId] (replacing an older file for the same clip) and prunes to the newest [KEEP]. */
    fun store(clipId: String, png: ByteArray): File = synchronized(lock) {
        dir.mkdirs()
        val safeId = clipId.replace(UNSAFE, "_")
        val existing = dir.listFiles().orEmpty().filter { it.isFile }
        existing.filter { it.name.substringAfter('_') == "$safeId.png" }.forEach { it.delete() }

        val highestOnDisk = existing.maxOfOrNull { order(it) } ?: -1L
        lastOrder = maxOf(System.currentTimeMillis(), lastOrder + 1, highestOnDisk + 1)
        val file = File(dir, "${lastOrder}_$safeId.png")
        file.writeBytes(png)

        dir.listFiles().orEmpty().filter { it.isFile }
            .sortedByDescending { order(it) }
            .drop(KEEP)
            .forEach { it.delete() }
        file
    }

    /** The stored file for [clipId], or null when it was never stored or has rotated out. */
    fun find(clipId: String): File? = synchronized(lock) {
        val name = "${clipId.replace(UNSAFE, "_")}.png"
        dir.listFiles().orEmpty()
            .filter { it.isFile && it.name.substringAfter('_') == name }
            .maxByOrNull { order(it) }
    }

    /** Deletes every stored file (sign-out, revoke): they are plaintext copies of clips. */
    fun clear(): Unit = synchronized(lock) {
        dir.listFiles().orEmpty().forEach { it.delete() }
    }

    private fun order(f: File): Long = f.name.substringBefore('_').toLongOrNull() ?: -1L

    private companion object {
        const val KEEP = 5
        val UNSAFE = Regex("[^A-Za-z0-9-]")
    }
}
