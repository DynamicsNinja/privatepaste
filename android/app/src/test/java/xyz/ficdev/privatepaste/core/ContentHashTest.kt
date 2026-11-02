package xyz.ficdev.privatepaste.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import java.io.File

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

    @Test
    fun fileHashVector() {
        val vector = Json.parseToJsonElement(File("../../docs/e2e-test-vectors.json").readText(Charsets.UTF_8))
            .jsonObject.getValue("fileHash").jsonObject
        val files = vector.getValue("files").jsonArray.map {
            val o = it.jsonObject
            ContentHash.FileDigest(
                o.getValue("name").jsonPrimitive.content, o.getValue("size").jsonPrimitive.long, o.getValue("sha256Hex").jsonPrimitive.content,
            )
        }
        assertEquals("0baeacb0a540c7f644e57744ae15f6df06f21ba4cdacf1444cec972fe60f4940", vector.getValue("expected").jsonPrimitive.content)
        assertEquals(vector.getValue("expected").jsonPrimitive.content, ContentHash.files(files))
        assertEquals("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", ContentHash.sha256Hex("hello".toByteArray()))
    }
}
