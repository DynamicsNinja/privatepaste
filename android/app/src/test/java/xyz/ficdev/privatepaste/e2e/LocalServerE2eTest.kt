package xyz.ficdev.privatepaste.e2e

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.HttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.core.ServerUrl
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.send.ClipSender
import xyz.ficdev.privatepaste.send.SendResult
import xyz.ficdev.privatepaste.storage.CryptoFile
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.InMemoryKeyProvider
import xyz.ficdev.privatepaste.storage.KeyAliases
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.storage.SessionStore
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import xyz.ficdev.privatepaste.sync.Backoff
import xyz.ficdev.privatepaste.sync.ClipApplier
import xyz.ficdev.privatepaste.sync.LocalCaptureClock
import xyz.ficdev.privatepaste.sync.OkHttpLinkFactory
import xyz.ficdev.privatepaste.sync.SessionManager
import xyz.ficdev.privatepaste.sync.SignInResult
import xyz.ficdev.privatepaste.sync.SyncEngine
import xyz.ficdev.privatepaste.sync.SyncState
import java.io.File
import java.time.Clock
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList

/**
 * End-to-end check of the real client classes against a LOCAL server. Skipped unless `PP_E2E_URL` is set, e.g.
 * `PP_E2E_URL=http://localhost:5299` with the server started with `PP_PASSWORD=dev` (see README "Local development").
 * It refuses any host other than localhost, so it can never touch a production server.
 */
@RunWith(RobolectricTestRunner::class)
class LocalServerE2eTest {

    @get:Rule val tmp = TemporaryFolder()

    private val password = "dev"
    private val client = OkHttpClient()
    private val applied = CopyOnWriteArrayList<ClipDto>()
    private lateinit var baseUrl: HttpUrl
    private lateinit var scope: CoroutineScope
    private var engine: SyncEngine? = null

    @Before
    fun setUp() {
        val url = System.getenv("PP_E2E_URL")
        assumeTrue("PP_E2E_URL is not set; skipping the local-server end-to-end test", !url.isNullOrBlank())
        baseUrl = ServerUrl.normalize(url!!)
        assumeTrue("The e2e test only talks to localhost", baseUrl.host in setOf("localhost", "127.0.0.1", "[::1]", "::1"))
        scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    }

    @After
    fun tearDown() {
        engine?.let { runBlocking { it.stop() } }
        if (::scope.isInitialized) scope.cancel()
    }

    @Test
    fun phoneAndPcSyncBothWaysAndRevoke() = runBlocking {
        val keys = InMemoryKeyProvider()
        val store = SessionStore(CryptoFile(File(tmp.root, "session.bin"), keys, KeyAliases.SESSION))
        val cache = HistoryCache(CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE))
        val queue = OutboundQueue(CryptoFile(File(tmp.root, "queue.bin"), keys, KeyAliases.QUEUE), tmp.newFolder("queue-images"), keys)
        val thumbs = ThumbnailStore(tmp.newFolder("thumbs"), keys)
        val manager = SessionManager(store, cache, thumbs, queue, apiFactory = { u, token -> PrivatePasteApi(u, token, client) })

        // 1. Sign in as the phone.
        assertEquals(SignInResult.Ok, manager.signIn(baseUrl.toString(), password, "e2e-phone"))
        val session = checkNotNull(manager.session.value)
        val phoneDeviceId = session.deviceId
        val phoneApi = PrivatePasteApi(baseUrl, session.token, client)

        val capture = LocalCaptureClock()
        val sender = ClipSender(phoneApi, queue, cache, capture, { }, Clock.systemUTC())
        val engine = SyncEngine(
            api = phoneApi, cache = cache, queue = queue, thumbs = thumbs,
            applier = ClipApplier { clip -> applied += clip; cache.markApplied(clip.lastCopiedAt, clip.hash); true },
            flushQueue = { sender.flushQueue() },
            links = OkHttpLinkFactory(open = { phoneApi.webSocket(it) }),
            ownDeviceId = phoneDeviceId, capture = capture, clock = Clock.systemUTC(), backoff = Backoff(), scope = scope,
        )
        this@LocalServerE2eTest.engine = engine
        engine.start()
        waitFor("engine connected") { engine.state.value == SyncState.CONNECTED }

        // 2. A second client signs in as the PC.
        val pcLogin = PrivatePasteApi(baseUrl, null, client).login(password, "e2e-pc")
        val pc = PrivatePasteApi(baseUrl, pcLogin.token, client)

        // 3. The PC uploads text; the phone applies it.
        val fromPc = "e2e from pc ${UUID.randomUUID()}"
        pc.uploadText(fromPc, null, backfill = false)
        waitFor("phone applies the pc clip") { applied.any { it.text == fromPc } }

        // 4. The phone sends text; the PC sees it in the history.
        val fromPhone = "e2e from phone ${UUID.randomUUID()}"
        assertEquals(SendResult.Sent, sender.sendText(fromPhone))
        val phoneClip = pc.clips(null, null).items.first { it.text == fromPhone }
        assertEquals(phoneDeviceId, phoneClip.sourceDeviceId)

        // 5. A delete on the PC reaches the phone's cache.
        assertTrue(pc.delete(phoneClip.id))
        waitFor("delete reaches the phone") { cache.items.value.none { it.id == phoneClip.id } }
        assertTrue(pc.clips(null, null).items.none { it.id == phoneClip.id })

        // 6. Revoking the phone from the PC emits `revoked`.
        val revoke = Request.Builder().url(baseUrl.newBuilder().addPathSegments("api/devices/$phoneDeviceId").build())
            .header("Authorization", "Bearer ${pcLogin.token}").delete().build()
        client.newCall(revoke).execute().use { assertEquals(204, it.code) }
        withTimeout(TIMEOUT_MS) { engine.revoked.first() }
        assertEquals(SyncState.SIGNED_OUT, engine.state.value)

        pc.logout()
    }

    private suspend fun waitFor(what: String, condition: () -> Boolean) {
        val deadline = System.nanoTime() + TIMEOUT_MS * 1_000_000
        while (!condition()) {
            if (System.nanoTime() > deadline) throw AssertionError("Timed out waiting for: $what")
            delay(50)
        }
    }

    private companion object {
        const val TIMEOUT_MS = 10_000L
    }
}
