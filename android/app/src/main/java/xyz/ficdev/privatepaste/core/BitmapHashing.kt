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
            val argb = IntArray(w * h)
            readable.getPixels(argb, 0, w, 0, 0, w, h) // unpremultiplied ARGB ints
            val rgba = ByteArray(argb.size * 4)
            for (i in argb.indices) {
                val p = argb[i]
                rgba[i * 4] = (p shr 16).toByte()
                rgba[i * 4 + 1] = (p shr 8).toByte()
                rgba[i * 4 + 2] = p.toByte()
                rgba[i * 4 + 3] = (p ushr 24).toByte()
            }
            return ContentHash.pixels(rgba, w, h)
        } finally {
            if (readable !== bitmap) readable.recycle()
        }
    }
}
