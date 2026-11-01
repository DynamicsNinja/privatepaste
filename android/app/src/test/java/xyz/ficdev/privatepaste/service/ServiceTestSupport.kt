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
import xyz.ficdev.privatepaste.core.crypto.VaultKey
import xyz.ficdev.privatepaste.storage.InMemoryKeyProvider
import xyz.ficdev.privatepaste.storage.KeyProvider
import xyz.ficdev.privatepaste.sync.SignInResult
import xyz.ficdev.privatepaste.sync.VaultState
import java.io.File
import java.net.InetAddress
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * The real Application with test seams: in-memory keys (Robolectric has no AndroidKeyStore) and, on every host,
 * a clipboard writer whose content URIs [WindowsHostFileProvider] serves (androidx FileProvider assumes '/').
 */
class TestPrivatePasteApp : PrivatePasteApp() {
    override fun keyProvider(): KeyProvider = InMemoryKeyProvider()

    override fun clipboardWriter(files: ClipImageFiles): ClipboardWriter =
        ClipboardWriter(this, files) { _, f ->
            Uri.parse("content://${ClipboardWriter.AUTHORITY}/clipboard/${Uri.encode(f.name)}")
        }

    companion object {
        /** Call in @Before so image URIs resolve (FileProvider can't map Robolectric's cacheDir; see ClipboardWriterTest). */
        fun setUpHostProvider() {
            Robolectric.setupContentProvider(WindowsHostFileProvider::class.java, ClipboardWriter.AUTHORITY)
        }
    }
}

/**
 * A local PrivatePaste server: login, logout, the vault check (server info, a vault under [vaultKey], the owner), an
 * empty sync, and a `/ws` that answers pings.
 */
class FakeServer {
    val server = MockWebServer().also { it.start(InetAddress.getByName("127.0.0.1"), 0) }
    val url: String get() = "http://127.0.0.1:${server.port}/"
    val wsRequests = AtomicInteger()

    /** The server's vault key; the phone gets a copy in [signInTo]. Only its id is on the wire here. */
    val vaultKey: VaultKey = VaultKey.create()

    private val vaultJson = """{"keyId":"${vaultKey.keyId}","kdf":{"alg":"argon2id","memoryKiB":65536,"iterations":3,""" +
        """"parallelism":1,"salt":"AAAAAAAAAAAAAAAAAAAAAA=="},"nonce":"AAAAAAAAAAAAAAAA",""" +
        """"wrappedKey":"${java.util.Base64.getEncoder().encodeToString(ByteArray(48))}"}"""

    /** Answers any other request first when it returns non-null (e.g. an image endpoint). */
    @Volatile var other: (RecordedRequest) -> MockResponse? = { null }

    /** The answer to the next `/ws` upgrade. */
    @Volatile var ws: () -> MockResponse = { MockResponse().withWebSocketUpgrade(PongListener(sockets)) }

    /** Server sides of the upgraded sockets, closed (1001) on [shutdown] so it does not wait for them. */
    private val sockets: MutableList<WebSocket> = java.util.Collections.synchronizedList(ArrayList())

    init {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = other(request) ?: when (request.path?.substringBefore('?')) {
                "/api/auth/login" -> json("""{"deviceId":"dev-phone","token":"tok-1"}""")
                "/api/auth/logout" -> MockResponse().setResponseCode(204)
                "/api/server-info" -> json("""{"protocol":2,"kinds":["text","link","image"],"limits":{"textKb":1024,"imageMb":20}}""")
                "/api/vault" -> json(vaultJson)
                "/api/me" -> json("""{"name":"owner","isOwner":true}""")
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

    /** Accepts the upgrade, then closes with [code] (1008: this device was revoked). */
    class CloseListener(private val code: Int) : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            webSocket.close(code, "revoked")
        }
    }

    companion object {
        fun json(body: String, code: Int = 200) =
            MockResponse().setResponseCode(code).setHeader("Content-Type", "application/json").setBody(body)
    }
}

/**
 * Signs in through the app's session manager (the UiDeps path the sign-in screen uses), then gives the phone the
 * server's vault key and settles the vault check, as unlocking does: until then the engine does not connect.
 */
fun PrivatePasteApp.signInTo(server: FakeServer) {
    val result = runBlocking { uiDeps.signIn(server.url, "owner", "dev", "Pixel Test") }
    assertEquals(SignInResult.Ok, result)
    runBlocking {
        graph.sessions.useVaultKey(VaultKey.fromBytes(server.vaultKey.export()))
        assertEquals(true, graph.sessions.checkVault())
    }
    assertEquals(VaultState.UNLOCKED, graph.sessions.vaultState.value)
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
