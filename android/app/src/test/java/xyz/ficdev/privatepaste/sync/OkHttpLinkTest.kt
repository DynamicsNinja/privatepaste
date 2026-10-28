package xyz.ficdev.privatepaste.sync

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.core.model.WsEvent
import java.io.IOException

/** The OkHttp-backed link against a MockWebServer WebSocket (real sockets; waits are bounded, never sleeps). */
class OkHttpLinkTest {
    private val server = MockWebServer()
    private val client = OkHttpClient()

    @Before
    fun setUp() = server.start()

    @After
    fun tearDown() = server.shutdown()

    private fun factory(capacity: Int = 64) =
        OkHttpLinkFactory(PrivatePasteApi(server.url("/"), "tok", client)::webSocket, capacity = capacity)

    /** Server side: sends [frames], then closes with [code] once the client's first message arrives. */
    private fun serverSocket(frames: List<String>, code: Int, reason: String = "") = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            frames.forEach { webSocket.send(it) }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (text == """{"type":"ping"}""") webSocket.send("""{"type":"pong"}""")
            webSocket.close(code, reason)
        }
    }

    @Test
    fun eventsArriveAndANormalCloseCompletes(): Unit = runBlocking {
        server.enqueue(MockResponse().withWebSocketUpgrade(serverSocket(listOf("""{"type":"clip.deleted","id":"a","seq":3}"""), 1001)))
        withTimeout(10_000) {
            val link = factory().connect()
            assertTrue(link.send("""{"type":"ping"}"""))
            assertEquals(listOf(WsEvent.ClipDeleted("a", 3), WsEvent.Pong), link.events.toList())
            link.close(1000, "")
        }
        assertEquals("Bearer tok", server.takeRequest().getHeader("Authorization"))
    }

    @Test
    fun close1008CompletesWithRevoked(): Unit = runBlocking {
        server.enqueue(MockResponse().withWebSocketUpgrade(serverSocket(emptyList(), 1008, "revoked")))
        withTimeout(10_000) {
            val link = factory().connect()
            link.send("""{"type":"hello"}""")
            try {
                link.events.toList()
                fail("expected RevokedException")
            } catch (_: RevokedException) {
            }
        }
    }

    @Test
    fun unauthorizedUpgradeThrowsUnauthorized(): Unit = runBlocking {
        server.enqueue(MockResponse().setResponseCode(401))
        withTimeout(10_000) {
            try {
                factory().connect()
                fail("expected Unauthorized")
            } catch (_: ApiException.Unauthorized) {
            }
        }
    }

    @Test
    fun overflowDropsTheLink(): Unit = runBlocking {
        val frames = List(50) { """{"type":"pong"}""" }
        val sent = CompletableDeferred<Unit>()
        server.enqueue(MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                frames.forEach { webSocket.send(it) }
                sent.complete(Unit)
            }
        }))
        withTimeout(10_000) {
            val link = factory(capacity = 4).connect()
            sent.await()
            (link as OkHttpLink).dropped.await()   // collect only once the reader has overflowed the buffer
            try {
                link.events.toList()
                fail("expected the overflow to fail the flow")
            } catch (_: IOException) {
            }
        }
    }
}
