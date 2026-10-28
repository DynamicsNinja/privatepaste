package xyz.ficdev.privatepaste.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ServerUrlTest {
    private fun rejected(input: String): String? =
        assertThrows(IllegalArgumentException::class.java) { ServerUrl.normalize(input) }.message

    @Test
    fun bareHostGetsTrailingSlash() {
        assertEquals("https://h/", ServerUrl.normalize("https://h").toString())
    }

    @Test
    fun subPathIsKeptWithTrailingSlash() {
        assertEquals("https://h/sub/", ServerUrl.normalize("https://h/sub").toString())
        assertEquals("https://h/sub/", ServerUrl.normalize("https://h/sub/").toString())
    }

    @Test
    fun inputIsTrimmedAndQueryDropped() {
        assertEquals("https://h/sub/", ServerUrl.normalize("  https://h/sub?x=1#f \n").toString())
    }

    @Test
    fun relativePathsResolveAgainstSubPath() {
        assertEquals("https://h/sub/api/clips", ServerUrl.normalize("https://h/sub").resolve("api/clips").toString())
    }

    @Test
    fun loopbackHttpIsAllowed() {
        assertEquals("http://localhost:5299/", ServerUrl.normalize("http://localhost:5299").toString())
        assertEquals("http://127.0.0.1:5299/", ServerUrl.normalize("http://127.0.0.1:5299").toString())
        assertEquals("http://[::1]:5299/", ServerUrl.normalize("http://[::1]:5299").toString())
    }

    @Test
    fun remoteHttpIsRejected() {
        val msg = "Use an https:// address (http:// is only allowed for localhost)."
        assertEquals(msg, rejected("http://example.com"))
        assertEquals(msg, rejected("http://localhost.evil.com"))
    }

    @Test
    fun blankIsRejected() {
        assertEquals("Enter a server address.", rejected(""))
        assertEquals("Enter a server address.", rejected("   "))
    }

    @Test
    fun garbageIsRejected() {
        for (s in listOf("example.com", "ftp://h", "https://", "not a url")) {
            assertEquals(s, "That doesn't look like a server address.", rejected(s))
        }
    }
}
