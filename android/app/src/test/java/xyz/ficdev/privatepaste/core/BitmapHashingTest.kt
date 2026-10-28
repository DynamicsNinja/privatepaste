package xyz.ficdev.privatepaste.core

import android.graphics.Bitmap
import android.graphics.Color
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(sdk = [35])
class BitmapHashingTest {
    @Test
    fun opaqueRedPixelMatchesReadmeVector() {
        val bmp = Bitmap.createBitmap(1, 1, Bitmap.Config.ARGB_8888)
        bmp.setPixel(0, 0, Color.argb(255, 255, 0, 0))
        assertEquals("8d833d22068fdf91eecbd0d310208548d1f9418c6513c572a7c7d01fc696c49a", BitmapHashing.hash(bmp))
    }

    @Test
    fun semiTransparentPixelsUseUnpremultipliedRgba() {
        val bmp = Bitmap.createBitmap(2, 1, Bitmap.Config.ARGB_8888)
        bmp.setPixel(0, 0, Color.argb(128, 200, 100, 50))
        bmp.setPixel(1, 0, Color.argb(255, 1, 2, 3))
        val rgba = ByteArray(8)
        for (x in 0 until 2) {
            val p = bmp.getPixel(x, 0) // unpremultiplied ARGB
            rgba[x * 4] = Color.red(p).toByte()
            rgba[x * 4 + 1] = Color.green(p).toByte()
            rgba[x * 4 + 2] = Color.blue(p).toByte()
            rgba[x * 4 + 3] = Color.alpha(p).toByte()
        }
        assertEquals(128, rgba[3].toInt() and 0xff)
        assertEquals(ContentHash.pixels(rgba, 2, 1), BitmapHashing.hash(bmp))
    }

    @Test
    fun hashIncludesDimensions() {
        val a = Bitmap.createBitmap(2, 1, Bitmap.Config.ARGB_8888)
        val b = Bitmap.createBitmap(1, 2, Bitmap.Config.ARGB_8888)
        assertNotEquals(BitmapHashing.hash(a), BitmapHashing.hash(b))
    }
}
