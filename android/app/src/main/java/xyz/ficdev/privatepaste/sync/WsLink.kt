package xyz.ficdev.privatepaste.sync

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.consumeAsFlow
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.core.model.ProtocolJson
import xyz.ficdev.privatepaste.core.model.WsEvent
import java.io.IOException
import kotlin.coroutines.resumeWithException
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/** The server closed the socket with 1008: this device was revoked or logged out. */
class RevokedException : Exception("Revoked by the server")

/** One open WebSocket to the server. */
interface WsLink {
    /**
     * Parsed messages, for a single collector. Completes normally when the socket closes (1001 and any other code
     * than 1008), completes with [RevokedException] on a 1008 close, and with an [IOException] when the connection
     * fails.
     */
    val events: Flow<WsEvent>

    /** Queues a text frame. False when the link is closed or failed. */
    fun send(text: String): Boolean

    fun close(code: Int, reason: String)
}

fun interface LinkFactory {
    /**
     * Opens a link. Throws [ApiException.Unauthorized] when the upgrade is refused with 401, and another exception
     * (IOException, timeout) on any other failure.
     */
    suspend fun connect(): WsLink
}

/**
 * Opens links with OkHttp through [open] (`PrivatePasteApi::webSocket`). [connect] gives up after [connectTimeout].
 * Leave OkHttp's `pingInterval` unset: the engine runs its own JSON ping, which is what tells it the link is alive.
 */
class OkHttpLinkFactory(
    private val open: (WebSocketListener) -> WebSocket,
    private val connectTimeout: Duration = 15.seconds,
    private val capacity: Int = DEFAULT_CAPACITY,
) : LinkFactory {

    // withTimeoutOrNull: a TimeoutCancellationException would look like the caller's own cancellation.
    override suspend fun connect(): WsLink = withTimeoutOrNull(connectTimeout) {
        suspendCancellableCoroutine<WsLink> { cont ->
            val link = OkHttpLink(capacity)
            val listener = object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    link.socket = webSocket
                    // Cancelled (timeout, stop) around the same moment: don't leak an open socket.
                    if (cont.isActive) cont.resume(link) { _, _, _ -> webSocket.cancel() } else webSocket.cancel()
                }

                override fun onMessage(webSocket: WebSocket, text: String) = link.deliver(ProtocolJson.parseWs(text))

                override fun onMessage(webSocket: WebSocket, bytes: ByteString) = Unit   // the server only sends text

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    link.finish(code)
                    webSocket.close(NORMAL_CLOSURE, null)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = link.finish(code)

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    if (cont.isActive) {
                        cont.resumeWithException(
                            if (response?.code == 401) ApiException.Unauthorized()
                            else IOException("WebSocket connect failed (${response?.code ?: t.javaClass.simpleName})", t),
                        )
                    } else {
                        link.fail(t)
                    }
                }
            }
            val socket = open(listener)
            link.socket = socket
            cont.invokeOnCancellation { socket.cancel() }
        }
    } ?: throw IOException("WebSocket connect timed out")

    private companion object {
        const val DEFAULT_CAPACITY = 256
        const val NORMAL_CLOSURE = 1000
    }
}

/**
 * OkHttp's listener runs on its reader thread, which must never block: messages go into a bounded channel, and a
 * reader that outruns the collector (a slow clipboard write, a stalled engine) drops the link instead of waiting.
 */
internal class OkHttpLink(capacity: Int) : WsLink {
    @Volatile var socket: WebSocket? = null

    private val channel = Channel<WsEvent>(capacity)

    /** Completed when the buffer overflowed (test hook). */
    internal val dropped = CompletableDeferred<Unit>()

    override val events: Flow<WsEvent> = channel.consumeAsFlow()

    fun deliver(event: WsEvent) {
        val result = channel.trySend(event)
        if (result.isSuccess || result.isClosed) return
        if (channel.close(IOException("WebSocket receive buffer overflowed"))) {
            socket?.cancel()
            dropped.complete(Unit)
        }
    }

    fun finish(code: Int) {
        channel.close(if (code == POLICY_VIOLATION) RevokedException() else null)
    }

    fun fail(t: Throwable) {
        channel.close(t as? IOException ?: IOException(t))
    }

    override fun send(text: String): Boolean = socket?.send(text) ?: false

    override fun close(code: Int, reason: String) {
        channel.close()
        val s = socket ?: return
        try {
            if (!s.close(code, reason)) s.cancel()
        } catch (_: IllegalArgumentException) {
            s.cancel()   // invalid close code or a reason over 123 bytes
        }
    }

    private companion object {
        const val POLICY_VIOLATION = 1008
    }
}
