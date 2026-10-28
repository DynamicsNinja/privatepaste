package xyz.ficdev.privatepaste.core

import java.security.MessageDigest

/** Content identity shared by every client; reproduces the README test vectors (see ContentHash.cs). */
object ContentHash {
    /** Hex of SHA-256("t:" + text) over UTF-8, with CRLF and lone CR converted to LF. */
    fun text(text: String): String {
        val normalized = text.replace("\r\n", "\n").replace('\r', '\n')
        return hex(MessageDigest.getInstance("SHA-256").digest(("t:" + normalized).toByteArray(Charsets.UTF_8)))
    }

    /** [rgba] must be RGBA8888, unpremultiplied, row-major, with no row padding. */
    fun pixels(rgba: ByteArray, width: Int, height: Int): String {
        val hasher = PixelHasher(width, height)
        hasher.update(rgba, 0, rgba.size)
        return hasher.finish()
    }

    /** Incremental [pixels]: feed the RGBA bytes in any chunking, then call [finish] once. */
    class PixelHasher(width: Int, height: Int) {
        private val digest = MessageDigest.getInstance("SHA-256").apply {
            update("i:${width}x$height:".toByteArray(Charsets.UTF_8))
        }

        fun update(rgba: ByteArray, offset: Int, length: Int) = digest.update(rgba, offset, length)

        fun finish(): String = hex(digest.digest())
    }

    private fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }
}
