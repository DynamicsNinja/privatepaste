package xyz.ficdev.privatepaste.service

import android.net.Uri
import android.os.Looper
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.Assert.assertEquals
import org.robolectric.Robolectric
import org.robolectric.Shadows.shadowOf
import xyz.ficdev.privatepaste.PrivatePasteApp
import xyz.ficdev.privatepaste.clipboard.ClipImageFiles
import xyz.ficdev.privatepaste.clipboard.ClipboardWriter
import xyz.ficdev.privatepaste.clipboard.WindowsHostFileProvider
import xyz.ficdev.privatepaste.storage.InMemoryKeyProvider
import xyz.ficdev.privatepaste.storage.KeyProvider
import xyz.ficdev.privatepaste.sync.SignInResult
import java.io.File
import java.net.InetAddress
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * The real Application with test seams: in-memory keys (Robolectric has no AndroidKeyStore) and, on a Windows host,
 * a clipboard writer whose content URIs [WindowsHostFileProvider] serves (androidx FileProvider assumes '/').
 */
class TestPrivatePasteApp : PrivatePasteApp() {
    override fun keyProvider(): KeyProvider = InMemoryKeyProvider()

    override fun clipboardWriter(files: ClipImageFiles): ClipboardWriter =
        if (File.separatorChar == '\\') {
            ClipboardWriter(this, files) { _, f ->
                Uri.parse("content://${ClipboardWriter.AUTHORITY}/clipboard/${Uri.encode(f.name)}")
            }
        } else {
            super.clipboardWriter(files)
        }

    companion object {
        /** Call in @Before on a Windows host, so image URIs resolve. */
        fun setUpHostProvider() {
            if (File.separatorChar == '\\') {
                Robolectric.setupContentProvider(WindowsHostFileProvider::class.java, ClipboardWriter.AUTHORITY)
            }
        }
    }
}

/** A local PrivatePaste server: login, logout, an empty sync, and a `/ws` that answers pings. */
class FakeServer {
    val server = MockWebServer().also { it.start(InetAddress.getByName("127.0.0.1"), 0) }
    val url: String get() = "http://127.0.0.1:${server.port}/"
    val wsRequests = AtomicInteger()

    /** The answer to the next `/ws` upgrade. */
    @Volatile var ws: () -> MockResponse = { MockResponse().withWebSocketUpgrade(PongListener(sockets)) }

    /** Server sides of the upgraded sockets, closed (1001) on [shutdown] so it does not wait for them. */
    private val sockets: MutableList<WebSocket> = java.util.Collections.synchronizedList(ArrayList())

    init {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.path?.substringBefore('?')) {
                "/api/auth/login" -> json("""{"deviceId":"dev-phone","token":"tok-1"}""")
                "/api/auth/logout" -> MockResponse().setResponseCode(204)
                "/api/sync" -> json("""{"clips":[],"deletedIds":[],"latestSeq":0,"hasMore":false}""")
                "/ws" -> {
                    wsRequests.incrementAndGet()
                    ws()
                }
                else -> MockResponse().setResponseCode(404)
            }
        }
    }

    fun shutdown() {
        synchronized(sockets) { sockets.forEach { runCatching { it.close(1001, null) } } }
        server.shutdown()
    }

    class PongListener(private val sockets: MutableList<WebSocket>) : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            sockets += webSocket
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(1000, null)
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (text == """{"type":"ping"}""") webSocket.send("""{"type":"pong"}""")
        }
    }

    companion object {
        fun json(body: String, code: Int = 200) =
            MockResponse().setResponseCode(code).setHeader("Content-Type", "application/json").setBody(body)
    }
}

/** Signs in through the app's session manager (the UiDeps path the sign-in screen uses). */
fun PrivatePasteApp.signInTo(server: FakeServer) {
    val result = runBlocking { uiDeps.signIn(server.url, "dev", "Pixel Test") }
    assertEquals(SignInResult.Ok, result)
}

/** Stops everything the app started, so no engine of this test keeps running into the next one. */
fun PrivatePasteApp.shutDownForTest() {
    appScope.cancel()
}

/** Polls [condition], running the main looper, for up to [timeoutMs] (real time: sockets are real). */
fun awaitUntil(message: String, timeoutMs: Long = 10_000, condition: () -> Boolean) {
    val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs)
    while (System.nanoTime() < deadline) {
        shadowOf(Looper.getMainLooper()).idle()
        if (condition()) return
        Thread.sleep(10)
    }
    shadowOf(Looper.getMainLooper()).idle()
    if (!condition()) throw AssertionError("Timed out: $message")
}

/** Runs the main looper for [ms] of real time (to show that something does NOT happen). */
fun idleFor(ms: Long) {
    val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(ms)
    while (System.nanoTime() < deadline) {
        shadowOf(Looper.getMainLooper()).idle()
        Thread.sleep(10)
    }
}
