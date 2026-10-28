package xyz.ficdev.privatepaste.core

import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

object ServerUrl {
    private val loopbackHosts = setOf("localhost", "127.0.0.1", "::1")

    /**
     * Validates the address typed by the user and returns it with a trailing `/`, keeping any sub-path.
     * Query and fragment are dropped. The exception message is shown in the UI as is.
     */
    fun normalize(input: String): HttpUrl {
        val text = input.trim()
        require(text.isNotEmpty()) { "Enter a server address." }
        val url = text.toHttpUrlOrNull()
            ?: throw IllegalArgumentException("That doesn't look like a server address.")
        require(url.username.isEmpty() && url.password.isEmpty()) { "Remove the user name and password from the address." }
        if (!url.isHttps && url.host !in loopbackHosts) {
            throw IllegalArgumentException("Use an https:// address (http:// is only allowed for localhost).")
        }
        val builder = url.newBuilder().query(null).fragment(null)
        if (url.pathSegments.last().isNotEmpty()) builder.addPathSegment("")
        return builder.build()
    }
}
