package xyz.ficdev.privatepaste.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class ContentHashTest {
    @Test
    fun textVectors() {
        assertEquals("1bec6ebade142b8ea787b2744bbb12d1915e0d0d5e8e31f7f5452911840de3a9", ContentHash.text("hello"))
        assertEquals("b77cc1fcd69063256ffc07884bde76c875b73e56a786925feedd7ba881f0616c", ContentHash.text("a\nb"))
        assertEquals("812328f074c3d5a025a329dd4674a676d83d5f926bd79af643a51e783ed3ac39", ContentHash.text("šećer 🚀"))
    }

    @Test
    fun pixelVector() {
        val rgba = byteArrayOf(255.toByte(), 0, 0, 255.toByte())
        assertEquals("8d833d22068fdf91eecbd0d310208548d1f9418c6513c572a7c7d01fc696c49a", ContentHash.pixels(rgba, 1, 1))
    }

    @Test
    fun crlfEqualsLf() {
        assertEquals(ContentHash.text("a\nb"), ContentHash.text("a\r\nb"))
        assertEquals("b77cc1fcd69063256ffc07884bde76c875b73e56a786925feedd7ba881f0616c", ContentHash.text("a\r\nb"))
    }

    @Test
    fun loneCrBecomesLf() {
        assertEquals(ContentHash.text("a\nb"), ContentHash.text("a\rb"))
    }

    @Test
    fun otherWhitespaceIsKept() {
        assertNotEquals(ContentHash.text("a\tb"), ContentHash.text("a b"))
        assertNotEquals(ContentHash.text("a\n\nb"), ContentHash.text("a\nb"))
    }
}
