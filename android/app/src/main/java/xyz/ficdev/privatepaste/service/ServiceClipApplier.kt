package xyz.ficdev.privatepaste.service

import android.content.Context
import android.util.Log
import kotlinx.coroutines.CancellationException
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.clipboard.ClipImageFiles
import xyz.ficdev.privatepaste.clipboard.ClipboardWriter
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.storage.DeliveryMode
import xyz.ficdev.privatepaste.sync.ClipApplier

/**
 * Delivers a clip from another device the way the settings say, read on every clip so a change applies at once:
 * [DeliveryMode.AUTOMATIC] writes the clipboard; [DeliveryMode.NOTIFICATION] posts the Copy notification. Image
 * clips are fetched first in both modes (in notification mode the PNG is kept in [files] for the Copy action); a
 * failed fetch returns false. A 401 propagates, so the engine treats it as a revoke. Runs on the engine's IO scope.
 */
class ServiceClipApplier(
    private val context: Context,
    private val deliveryMode: () -> DeliveryMode,
    private val writer: ClipboardWriter,
    private val files: ClipImageFiles,
    private val fetchImage: suspend (clipId: String) -> ByteArray,
) : ClipApplier {

    override suspend fun apply(clip: ClipDto): Boolean {
        val mode = deliveryMode()
        if (clip.kind == ClipKind.IMAGE) {
            val png = fetch(clip.id) ?: return false
            return when (mode) {
                DeliveryMode.AUTOMATIC -> {
                    writer.writeImage(clip.id, png)
                    true
                }
                DeliveryMode.NOTIFICATION -> {
                    files.store(clip.id, png)
                    Notifications.postClip(context, clip)
                }
            }
        }
        val text = clip.text ?: return false
        return when (mode) {
            DeliveryMode.AUTOMATIC -> {
                writer.writeText(text)
                true
            }
            DeliveryMode.NOTIFICATION -> Notifications.postClip(context, clip)
        }
    }

    private suspend fun fetch(id: String): ByteArray? = try {
        fetchImage(id)
    } catch (e: CancellationException) {
        throw e
    } catch (e: ApiException.Unauthorized) {
        throw e
    } catch (e: Exception) {
        Log.w(TAG, "Could not fetch the image (${e.javaClass.simpleName})")
        null
    }

    private companion object {
        const val TAG = "ServiceClipApplier"
    }
}
