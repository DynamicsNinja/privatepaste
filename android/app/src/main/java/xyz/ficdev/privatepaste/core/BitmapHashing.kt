package xyz.ficdev.privatepaste.core

import android.graphics.Bitmap

object BitmapHashing {
    /** Hashes the pixels as RGBA8888, unpremultiplied, row-major (README "Content hash"). */
    fun hash(bitmap: Bitmap): String {
        // Hardware bitmaps cannot be read with getPixels.
        val readable = if (bitmap.config == Bitmap.Config.HARDWARE) {
            bitmap.copy(Bitmap.Config.ARGB_8888, false) ?: throw IllegalArgumentException("Cannot read bitmap pixels")
        } else {
            bitmap
        }
        try {
            val w = readable.width
            val h = readable.height
            // One row at a time keeps memory at O(width) even for a 50 MP image.
            val hasher = ContentHash.PixelHasher(w, h)
            val argb = IntArray(w)
            val rgba = ByteArray(w * 4)
            for (y in 0 until h) {
                readable.getPixels(argb, 0, w, 0, y, w, 1) // unpremultiplied ARGB ints
                for (x in 0 until w) {
                    val p = argb[x]
                    rgba[x * 4] = (p shr 16).toByte()
                    rgba[x * 4 + 1] = (p shr 8).toByte()
                    rgba[x * 4 + 2] = p.toByte()
                    rgba[x * 4 + 3] = (p ushr 24).toByte()
                }
                hasher.update(rgba, 0, rgba.size)
            }
            return hasher.finish()
        } finally {
            if (readable !== bitmap) readable.recycle()
        }
    }
}
