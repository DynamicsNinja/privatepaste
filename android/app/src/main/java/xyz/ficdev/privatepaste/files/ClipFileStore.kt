package xyz.ficdev.privatepaste.files

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.core.FileNames
import xyz.ficdev.privatepaste.core.crypto.ClipCodec
import xyz.ficdev.privatepaste.core.model.ClipDto
import java.io.File
import java.io.IOException

/**
 * The opened files of file clips, one folder per clip under [root] (`cacheDir/files`, served by the FileProvider as
 * `files/<clipId>/<name>`). Nothing is downloaded until Copy, Save or Share asks for it. These are plaintext copies:
 * the host prunes them to the cached clips and clears them with the session.
 */
class ClipFileStore(private val root: File) {

    private val lock = Mutex()

    /** Clip folders being written right now: [prune] leaves them alone. Guards [generation] too. */
    private val busy = HashSet<String>()

    /** Bumped by [clear]: a download that started before it must not leave its files behind. */
    private var generation = 0

    /**
     * The clip's files, downloaded and opened on first use: blob `i` comes from [fetch] and is decrypted into
     * `<name>.part`, renamed once it authenticates. Names are made safe and unique within the clip, so a name can't
     * leave the clip's folder. Any failure removes the clip's folder (no partial file is left) and propagates.
     * @throws IllegalArgumentException when [clip] lists no files.
     */
    suspend fun materialize(clip: ClipDto, fetch: suspend (Int) -> ByteArray, codec: ClipCodec): List<File> = lock.withLock {
        val infos = clip.files
        require(!infos.isNullOrEmpty()) { "Not a file clip" }
        val folder = folderName(clip.id)
        val dir = File(root, folder)
        // Unique ignoring case, so two names never land on one file on a case-insensitive file system.
        val taken = HashSet<String>()
        val targets = infos.map { info ->
            val name = FileNames.unique(FileNames.safe(info.name)) { it.lowercase() in taken }
            taken += name.lowercase()
            File(dir, name)
        }
        val complete = withContext(Dispatchers.IO) { targets.zip(infos).all { (f, info) -> f.isFile && f.length() == info.size } }
        if (complete) return@withLock targets

        val started = synchronized(busy) {
            busy += folder
            generation
        }
        try {
            withContext(Dispatchers.IO) {
                dir.deleteRecursively()
                dir.mkdirs()
            }
            for ((i, target) in targets.withIndex()) {
                val sealed = fetch(i)
                withContext(Dispatchers.IO) {
                    val part = File(dir, target.name + ".part")
                    part.outputStream().buffered().use { codec.decodeBlobTo(sealed, it) }
                    if (!part.renameTo(target)) throw IOException("Cannot finish a downloaded file")
                }
            }
            if (synchronized(busy) { generation } != started) throw IOException("Cleared while downloading")
            targets
        } catch (e: Throwable) {
            withContext(NonCancellable + Dispatchers.IO) { dir.deleteRecursively() }
            throw e
        } finally {
            synchronized(busy) { busy -= folder }
        }
    }

    /** Deletes the folders of clips whose id is not in [keep] (and anything else under [root]). */
    fun prune(keep: Set<String>) {
        val kept = keep.mapTo(HashSet(), ::folderName)
        val skip = synchronized(busy) { busy.toSet() }
        root.listFiles().orEmpty()
            .filter { it.name !in kept && it.name !in skip }
            .forEach { it.deleteRecursively() }
    }

    /** Deletes every stored file (sign-out, reset): they are plaintext copies of clips. A download in flight fails. */
    fun clear() {
        synchronized(busy) { generation++ }
        root.listFiles().orEmpty().forEach { it.deleteRecursively() }
    }

    private companion object {
        val UNSAFE = Regex("[^A-Za-z0-9-]")

        /** A clip id from the server becomes a plain folder name: it can't name a parent or nest. */
        fun folderName(clipId: String) = clipId.replace(UNSAFE, "_").ifEmpty { "_" }
    }
}
