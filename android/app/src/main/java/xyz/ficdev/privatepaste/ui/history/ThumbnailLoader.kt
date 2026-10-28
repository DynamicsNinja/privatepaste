package xyz.ficdev.privatepaste.ui.history

import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import kotlin.coroutines.CoroutineContext

/**
 * Row thumbnails, by content hash: an in-memory LRU of [capacity] decoded bitmaps, then the encrypted
 * [ThumbnailStore], then `GET /api/clips/{id}/thumbnail` (stored for next time). Failures give null.
 */
class ThumbnailLoader(
    private val store: ThumbnailStore?,
    private val remote: () -> ClipRemote?,
    private val decode: (ByteArray) -> ImageBitmap? = ::decodePng,
    private val io: CoroutineContext = Dispatchers.IO,
    capacity: Int = 100,
) {
    private val memory = LruCache<String, ImageBitmap>(capacity)

    /** The decoded thumbnail if it is already in memory. */
    fun peek(hash: String): ImageBitmap? = memory.get(hash)

    suspend fun load(clip: ClipDto): ImageBitmap? {
        memory.get(clip.hash)?.let { return it }
        return withContext(io) {
            val bitmap = store?.get(clip.hash)?.let(decode) ?: fetch(clip)
            bitmap?.also { memory.put(clip.hash, it) }
        }
    }

    private suspend fun fetch(clip: ClipDto): ImageBitmap? {
        val api = remote() ?: return null
        val png = try {
            api.thumbnail(clip.id)
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            return null
        }
        val bitmap = decode(png) ?: return null
        store?.put(clip.hash, png)
        return bitmap
    }

    private companion object {
        fun decodePng(bytes: ByteArray): ImageBitmap? =
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
    }
}
