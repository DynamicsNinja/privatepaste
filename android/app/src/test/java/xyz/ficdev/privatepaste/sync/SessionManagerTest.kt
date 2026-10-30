package xyz.ficdev.privatepaste.sync

import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import androidx.test.core.app.ApplicationProvider
import xyz.ficdev.privatepaste.storage.SettingsStore
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.storage.CryptoFile
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.InMemoryKeyProvider
import xyz.ficdev.privatepaste.storage.KeyAliases
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.storage.SessionStore
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import java.io.File
import java.net.InetAddress
import java.time.Instant

@RunWith(RobolectricTestRunner::class)
class SessionManagerTest {

    @get:Rule val tmp = TemporaryFolder()

    private val keys = InMemoryKeyProvider()
    private val server = MockWebServer()
    private val client = OkHttpClient()
    private lateinit var store: SessionStore
    private lateinit var cache: HistoryCache
    private lateinit var queue: OutboundQueue
    private lateinit var thumbs: ThumbnailStore
    private lateinit var url: String

    @Before
    fun setUp() {
        server.start(InetAddress.getByName("127.0.0.1"), 0)
        url = "http://127.0.0.1:${server.port}/"
        store = SessionStore(CryptoFile(File(tmp.root, "session.bin"), keys, KeyAliases.SESSION))
        cache = HistoryCache(CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE))
        queue = OutboundQueue(CryptoFile(File(tmp.root, "queue.bin"), keys, KeyAliases.QUEUE), tmp.newFolder("queue-images"), keys)
        thumbs = ThumbnailStore(tmp.newFolder("thumbs"), keys)
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    // runBlocking, not runTest: these talk to a real MockWebServer, and virtual time would fire the logout timeout early.
    private fun manager(hints: SignInHints = InMemorySignInHints()) = SessionManager(
        store, cache, thumbs, queue,
        apiFactory = { baseUrl, token -> PrivatePasteApi(baseUrl, token, client) },
        hints = hints,
    )

    private fun json(body: String, code: Int = 200) =
        MockResponse().setResponseCode(code).setHeader("Content-Type", "application/json").setBody(body)

    private fun seedLocalData() {
        cache.upsert(
            ClipDto("old", 1, ClipKind.TEXT, "old", "hash-old", sizeBytes = 3, sourceDeviceId = "x",
                lastCopiedAt = Instant.parse("2026-10-01T00:00:00Z")),
            fromSync = true,
        )
        cache.markApplied(Instant.parse("2026-10-01T00:00:00Z"), "hash-old")
        cache.save()
        queue.enqueueText("queued", Instant.parse("2026-10-01T00:00:00Z"))
        thumbs.put("hash-old", byteArrayOf(1))
    }

    private fun assertLocalDataCleared() {
        assertTrue(cache.items.value.isEmpty())
        assertNull(cache.lastAppliedAt)
        assertEquals(0L, cache.highestSeq)
        assertTrue(queue.isEmpty)
        assertNull(thumbs.get("hash-old"))
        val reloaded = HistoryCache(CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE))
        assertTrue(reloaded.items.value.isEmpty())
    }

    @Test
    fun wrongPasswordAndRateLimit(): Unit = runBlocking {
        val m = manager()
        server.enqueue(MockResponse().setResponseCode(401))
        assertEquals(SignInResult.WrongPassword, m.signIn(url, "Ana", "nope", "Pixel"))
        server.enqueue(MockResponse().setResponseCode(429))
        assertEquals(SignInResult.RateLimited, m.signIn(url, "Ana", "nope", "Pixel"))
        assertNull(m.session.value)
        assertNull(store.load())
    }

    @Test
    fun rejectedAndUnreachable(): Unit = runBlocking {
        val m = manager()
        server.enqueue(json("""{"title":"Bad request","detail":"Device name too long"}""", code = 400))
        assertEquals(SignInResult.Failed("Device name too long"), m.signIn(url, "Ana", "dev", "x".repeat(60)))

        server.shutdown()
        assertEquals(
            SignInResult.Unreachable("Can't reach the server. Check the address and your connection."),
            m.signIn(url, "Ana", "dev", "Pixel"),
        )
    }

    @Test
    fun plainHttpIsInvalidUrl(): Unit = runBlocking {
        seedLocalData()
        val result = manager().signIn("http://example.com", "Ana", "dev", "Pixel")

        assertEquals(SignInResult.InvalidUrl("Use an https:// address (http:// is only allowed for localhost)."), result)
        assertEquals(0, server.requestCount)
        assertEquals(1, cache.items.value.size)   // a failed sign-in leaves local data alone
    }

    @Test
    fun successfulSignInClearsOldDataAndSavesSession(): Unit = runBlocking {
        seedLocalData()
        val m = manager()
        server.enqueue(json("""{"deviceId":"dev-1","token":"tok-1"}"""))

        assertEquals(SignInResult.Ok, m.signIn("  ${url.removeSuffix("/")}  ", "Ana", "dev", "Pixel 9"))

        val expected = StoredSession(url, "dev-1", "Pixel 9", "tok-1")
        assertEquals(expected, m.session.value)
        assertEquals(expected, store.load())
        assertLocalDataCleared()
        val body = server.takeRequest().body.readUtf8()
        assertTrue(body, body.contains("\"platform\":\"android\"") && body.contains("\"deviceName\":\"Pixel 9\""))
        assertEquals(url, m.lastServerUrl)
        assertEquals("Pixel 9", m.lastDeviceName)
    }

    @Test
    fun signInRemembersTheLastUsername(): Unit = runBlocking {
        server.enqueue(json("""{"deviceId":"dev-1","token":"tok-1"}"""))
        val settings = SettingsStore(ApplicationProvider.getApplicationContext())
        val m = SessionManager(
            store, cache, thumbs, queue,
            apiFactory = { baseUrl, token -> PrivatePasteApi(baseUrl, token, client) },
            settings = settings,
        )
        assertEquals(null, m.lastUsername)
        assertEquals(SignInResult.Ok, m.signIn(url, "Ana", "dev", "Pixel 9"))
        assertEquals("Ana", m.lastUsername)
        assertEquals("Ana", SettingsStore(ApplicationProvider.getApplicationContext()).lastUsername)
        assertTrue(server.takeRequest().body.readUtf8().contains("\"username\":\"Ana\""))
    }

    @Test
    fun loadsStoredSessionOnStart(): Unit = runBlocking {
        val saved = StoredSession(url, "dev-1", "Pixel", "tok")
        store.save(saved)
        assertEquals(saved, manager().session.value)
    }

    @Test
    fun onRevokedKeepsServerUrlForTheForm(): Unit = runBlocking {
        val hints = InMemorySignInHints()
        val m = manager(hints)
        server.enqueue(json("""{"deviceId":"dev-1","token":"tok-1"}"""))
        assertEquals(SignInResult.Ok, m.signIn(url, "Ana", "dev", "Pixel 9"))
        seedLocalData()

        m.onRevoked()

        assertNull(m.session.value)
        assertNull(store.load())
        assertLocalDataCleared()
        assertEquals(url, m.lastServerUrl)
        assertEquals("Pixel 9", m.lastDeviceName)
        // Survives a restart through the hints store.
        assertEquals(url, manager(hints).lastServerUrl)
        assertEquals(1, server.requestCount)   // no logout call for a revoked token
    }

    @Test
    fun signOutLogsOutBestEffortAndClearsEverything(): Unit = runBlocking {
        val m = manager()
        server.enqueue(json("""{"deviceId":"dev-1","token":"tok-1"}"""))
        assertEquals(SignInResult.Ok, m.signIn(url, "Ana", "dev", "Pixel 9"))
        seedLocalData()
        server.enqueue(MockResponse().setResponseCode(500))

        m.signOut()

        server.takeRequest()
        val logout = server.takeRequest()
        assertEquals("/api/auth/logout", logout.path)
        assertEquals("Bearer tok-1", logout.getHeader("Authorization"))
        assertNull(m.session.value)
        assertNull(store.load())
        assertLocalDataCleared()
        assertNull(m.lastServerUrl)
    }
}
