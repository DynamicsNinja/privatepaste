package xyz.ficdev.privatepaste.core.crypto

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import androidx.exifinterface.media.ExifInterface
import xyz.ficdev.privatepaste.core.BitmapHashing
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import kotlin.math.max
import kotlin.math.roundToInt

/** An image ready to seal: the upright PNG, its size, its pixel [contentHash] and a PNG thumbnail fitting 256x256. */
class PreparedImage(val png: ByteArray, val width: Int, val height: Int, val thumbPng: ByteArray, val contentHash: String)

/** The image can't be decoded on this phone. */
class UnreadableImageException : Exception("The image can't be decoded.")

/**
 * Turns any image the phone can decode into what every client stores: an upright PNG (EXIF orientation applied, since
 * the server can no longer do it) and a small PNG thumbnail. Same rules and copy as the desktop's ImagePreparation.
 * Decodes the whole image, so call it off the main thread.
 */
object ImagePreparation {
    const val MAX_PIXELS = 50_000_000L
    const val THUMB_SIZE = 256
    const val TOO_MANY_PIXELS = "Images over 50 megapixels aren't synced."

    fun tooLargeMessage(imageMb: Long) = "Images over $imageMb MB aren't synced."

    /**
     * @throws ClipTooLargeException over [MAX_PIXELS], or when the PNG is over [maxBytes].
     * @throws UnreadableImageException when the bytes aren't an image this phone can decode.
     */
    fun prepare(bytes: ByteArray, maxBytes: Long): PreparedImage {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) throw UnreadableImageException()
        // Checked before decoding: a huge image would not fit in memory anyway.
        if (bounds.outWidth.toLong() * bounds.outHeight > MAX_PIXELS) throw ClipTooLargeException(TOO_MANY_PIXELS)

        var decoded: Bitmap? = null
        var upright: Bitmap? = null
        var thumb: Bitmap? = null
        try {
            decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply {
                inPreferredConfig = Bitmap.Config.ARGB_8888
            }) ?: throw UnreadableImageException()
            upright = orient(decoded, orientation(bytes))
            // Two full-size bitmaps at once is the peak: free the unrotated one as soon as the copy exists.
            if (upright !== decoded) {
                decoded.recycle()
                decoded = null
            }
            val png = upright.toPng()
            if (png.size > maxBytes) throw ClipTooLargeException(tooLargeMessage(maxBytes / MIB))
            thumb = thumbnailOf(upright)
            return PreparedImage(png, upright.width, upright.height, thumb.toPng(), BitmapHashing.hash(upright))
        } catch (_: OutOfMemoryError) {
            throw UnreadableImageException()
        } finally {
            if (thumb !== upright) thumb?.recycle()
            if (upright !== decoded) upright?.recycle()
            decoded?.recycle()
        }
    }

    private fun orientation(bytes: ByteArray): Int = try {
        ExifInterface(ByteArrayInputStream(bytes)).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)
    } catch (_: Exception) {
        ExifInterface.ORIENTATION_NORMAL   // no or unreadable EXIF: the pixels are taken as they are
    }

    private fun orient(bitmap: Bitmap, orientation: Int): Bitmap {
        val matrix = Matrix()
        when (orientation) {
            ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> matrix.setScale(-1f, 1f)
            ExifInterface.ORIENTATION_ROTATE_180 -> matrix.setRotate(180f)
            ExifInterface.ORIENTATION_FLIP_VERTICAL -> matrix.setScale(1f, -1f)
            ExifInterface.ORIENTATION_TRANSPOSE -> matrix.apply { setRotate(90f); postScale(-1f, 1f) }
            ExifInterface.ORIENTATION_ROTATE_90 -> matrix.setRotate(90f)
            ExifInterface.ORIENTATION_TRANSVERSE -> matrix.apply { setRotate(-90f); postScale(-1f, 1f) }
            ExifInterface.ORIENTATION_ROTATE_270 -> matrix.setRotate(-90f)
            else -> return bitmap
        }
        return Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
    }

    /** Scaled down to fit [THUMB_SIZE] on both sides; a smaller image is kept as it is. */
    private fun thumbnailOf(bitmap: Bitmap): Bitmap {
        val longest = max(bitmap.width, bitmap.height)
        if (longest <= THUMB_SIZE) return bitmap
        val scale = THUMB_SIZE.toDouble() / longest
        val w = (bitmap.width * scale).roundToInt().coerceIn(1, THUMB_SIZE)
        val h = (bitmap.height * scale).roundToInt().coerceIn(1, THUMB_SIZE)
        return Bitmap.createScaledBitmap(bitmap, w, h, true)
    }

    private fun Bitmap.toPng(): ByteArray =
        ByteArrayOutputStream().also { check(compress(Bitmap.CompressFormat.PNG, 100, it)) { "PNG encoding failed" } }.toByteArray()

    private const val MIB = 1024L * 1024
}
