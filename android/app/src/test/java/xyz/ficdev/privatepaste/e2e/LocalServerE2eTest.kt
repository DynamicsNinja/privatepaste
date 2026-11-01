package xyz.ficdev.privatepaste.e2e

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import android.graphics.Bitmap
import android.graphics.Color
import okhttp3.HttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.GraphicsMode
import xyz.ficdev.privatepaste.api.KeyResetException
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.core.ServerUrl
import xyz.ficdev.privatepaste.core.crypto.ClipCodec
import xyz.ficdev.privatepaste.core.crypto.PassphraseWrap
import xyz.ficdev.privatepaste.core.crypto.VaultKey
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ServerLimits
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
import java.io.ByteArrayOutputStream
import java.io.File
import java.time.Clock
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList

/**
 * End-to-end check of the real client classes against a LOCAL server. Skipped unless `PP_E2E_URL` is set, e.g.
 * `PP_E2E_URL=http://localhost:5299` with the server started with `PP_PASSWORD=dev` (see README "Local development").
 * It refuses any host other than localhost, so it can never touch a production server. Both clients share one vault
 * key: a server without a vault gets one under [PASSPHRASE]; a server whose vault was set up otherwise fails the unlock.
 */
@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
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
        assertEquals(SignInResult.Ok, manager.signIn(baseUrl.toString(), "owner", password, "e2e-phone"))
        val session = checkNotNull(manager.session.value)
        val phoneDeviceId = session.deviceId
        val phoneApi = PrivatePasteApi(baseUrl, session.token, client)
        val key = phoneApi.vault()?.let { PassphraseWrap.unwrap(it, PASSPHRASE) }
            ?: VaultKey.create().also { phoneApi.putVault(PassphraseWrap.wrap(it, PASSPHRASE)) }
        val codec = ClipCodec(key)

        val capture = LocalCaptureClock()
        val sender = ClipSender(
            phoneApi, { codec }, { phoneApi.serverInfo()!!.limits }, queue, cache, capture, { }, Clock.systemUTC(),
        )
        val engine = SyncEngine(
            api = phoneApi, cache = cache, queue = queue, thumbs = thumbs,
            applier = ClipApplier { clip -> applied += clip; cache.markApplied(clip.lastCopiedAt, clip.hash); true },
            flushQueue = { sender.flushQueue() },
            links = OkHttpLinkFactory(open = { phoneApi.webSocket(it) }),
            codec = { codec },
            ownDeviceId = phoneDeviceId, capture = capture, clock = Clock.systemUTC(), backoff = Backoff(), scope = scope,
        )
        this@LocalServerE2eTest.engine = engine
        engine.start()
        waitFor("engine connected") { engine.state.value == SyncState.CONNECTED }

        // 2. A second client signs in as the PC.
        val pcLogin = PrivatePasteApi(baseUrl, null, client).login("owner", password, "e2e-pc")
        val pc = PrivatePasteApi(baseUrl, pcLogin.token, client)

        try {
            // 3. The PC uploads text; the phone applies it.
            val fromPc = "e2e from pc ${UUID.randomUUID()}"
            val sealed = codec.encodeText(fromPc, null, backfill = false)
            pc.uploadClip(sealed.meta, sealed.thumb, sealed.blobs)
            waitFor("phone applies the pc clip") { applied.any { it.text == fromPc } }

            // 4. The phone sends text; the PC sees it in the history.
            val fromPhone = "e2e from phone ${UUID.randomUUID()}"
            assertEquals(SendResult.Sent, sender.sendText(fromPhone))
            val phoneClip = pc.clips(null).items.map(codec::decode).first { it.text == fromPhone }
            assertEquals(phoneDeviceId, phoneClip.sourceDeviceId)

            // 5. A delete on the PC reaches the phone's cache.
            assertTrue(pc.delete(phoneClip.id))
            waitFor("delete reaches the phone") { cache.items.value.none { it.id == phoneClip.id } }
            assertTrue(pc.clips(null).items.none { it.id == phoneClip.id })

            // 6. Revoking the phone from the PC emits `revoked`.
            val revoke = Request.Builder().url(baseUrl.newBuilder().addPathSegments("api/devices/$phoneDeviceId").build())
                .header("Authorization", "Bearer ${pcLogin.token}").delete().build()
            client.newCall(revoke).execute().use { assertEquals(204, it.code) }
            withTimeout(TIMEOUT_MS) { engine.revoked.first() }
            assertEquals(SyncState.SIGNED_OUT, engine.state.value)
        } finally {
            // Best-effort cleanup of the pc device and its clips.
            runCatching {
                pc.clips(null).items.map(codec::decode).filter { it.text?.startsWith("e2e from ") == true }.forEach { pc.delete(it.id) }
            }
            runCatching { pc.logout() }
        }
    }

    /**
     * Encryption end to end with the real client classes: set up, text and an image one session writes and another
     * reads, a handover, then a reset that locks the first session out. Two sign-ins, to stay under the server's 5 per
     * minute. Ends with a vault under [PASSPHRASE], which the test above relies on.
     */
    @Test
    fun encryptionSetupUnlockHandoverAndReset() = runBlocking {
        val first = Device("first")
        val second = Device("second")
        try {
            // 1. The owner signs in and sets up encryption (or, on a server that has a vault already, resets it).
            assertEquals(SignInResult.Ok, first.manager.signIn(baseUrl.toString(), "owner", password, "e2e-first"))
            val firstApi = first.api()
            if (firstApi.vault() == null) {
                assertNull(first.manager.setUp(PASSPHRASE, PASSPHRASE))
            } else {
                val fresh = VaultKey.create()
                firstApi.resetVault(PassphraseWrap.wrap(fresh, PASSPHRASE))
                first.manager.useVaultKey(fresh)
            }
            val firstKey = checkNotNull(first.manager.vaultKey.value)
            assertEquals(firstKey.keyId, firstApi.vault()?.keyId)
            val codec = ClipCodec(firstKey)
            val limits = checkNotNull(firstApi.serverInfo()).limits
            val engine = first.engine(codec).also { this@LocalServerE2eTest.engine = it }
            engine.start()
            waitFor("first session connected") { engine.state.value == SyncState.CONNECTED }

            // 2. A text clip goes up from Android.
            val text = "e2e sealed text ${UUID.randomUUID()}"
            val sealed = codec.encodeText(text, null, backfill = false)
            val uploaded = firstApi.uploadClip(sealed.meta, sealed.thumb, sealed.blobs)
            assertEquals(ClipKind.TEXT, uploaded.kind)
            assertEquals(firstKey.keyId, uploaded.keyId)
            assertEquals(text.toByteArray(Charsets.UTF_8).size.toLong(), uploaded.sizeBytes)
            assertEquals(sealed.meta.hash, uploaded.hash)
            assertEquals(false, uploaded.hasThumb)

            // 3. A second session unlocks with the passphrase and reads it back decoded.
            assertEquals(SignInResult.Ok, second.manager.signIn(baseUrl.toString(), "owner", password, "e2e-second"))
            assertNull(second.manager.unlock(PASSPHRASE))
            val secondApi = second.api()
            val secondKey = checkNotNull(second.manager.vaultKey.value)
            assertEquals(firstKey.keyId, secondKey.keyId)
            val read = secondApi.clips(null).items.map(ClipCodec(secondKey)::decode).first { it.id == uploaded.id }
            assertEquals(text, read.text)

            // 4. A handover session 1 makes is taken by session 2, once.
            val (created, qr) = first.manager.createHandover()
            assertNull(second.manager.unlockWithQr(qr))
            val handedKey = checkNotNull(second.manager.vaultKey.value)
            assertEquals(firstKey.keyId, handedKey.keyId)
            assertNotNull("a second take of the same code must fail", second.manager.unlockWithQr(qr))
            assertNull(secondApi.takeHandover(created.id))
            val codec2 = ClipCodec(checkNotNull(second.manager.vaultKey.value))
            assertEquals(text, codec2.decode(secondApi.clips(null).items.first { it.id == uploaded.id }).text)

            // 5. An image goes up from session 1; session 2 downloads, decrypts and compares thumb and blob.
            val image = codec.encodeImage(testPng(), null, backfill = false, limits)
            val imageClip = firstApi.uploadClip(image.meta, image.thumb, image.blobs)
            assertEquals(ClipKind.IMAGE, imageClip.kind)
            assertTrue(imageClip.hasThumb)
            assertEquals(1, imageClip.blobCount)
            val png = codec.decodeBlob(image.blobs.single())
            assertEquals(png.size.toLong(), imageClip.sizeBytes)
            val seen = codec2.decode(secondApi.clips(null).items.first { it.id == imageClip.id })
            assertEquals(ClipKind.IMAGE, seen.kind)
            assertEquals(IMAGE_WIDTH, seen.width)
            assertEquals(IMAGE_HEIGHT, seen.height)
            val sealedBlob = secondApi.blob(imageClip.id, 0)
            val sealedThumb = secondApi.thumb(imageClip.id)
            assertArrayEquals(image.blobs.single(), sealedBlob)   // the server stores ciphertext verbatim
            assertArrayEquals(image.thumb, sealedThumb)
            assertArrayEquals(png, codec2.decodeBlob(sealedBlob))
            assertArrayEquals(codec.decodeThumb(image.thumb!!), codec2.decodeThumb(sealedThumb))

            // 6. The owner resets encryption (as the web page does): session 1 hears it, and its next upload is refused.
            val newKey = VaultKey.create()
            secondApi.resetVault(PassphraseWrap.wrap(newKey, PASSPHRASE))
            assertEquals(firstKey.keyId, withTimeout(TIMEOUT_MS) { engine.keyReset.first() })
            val stale = codec.encodeText("e2e after reset ${UUID.randomUUID()}", null, backfill = false)
            val refused = assertThrows(KeyResetException::class.java) {
                runBlocking { firstApi.uploadClip(stale.meta, stale.thumb, stale.blobs) }
            }
            assertEquals(409, refused.status)
            assertEquals("Encryption was reset", refused.title)
            assertTrue(secondApi.clips(null).items.isEmpty())   // the reset wiped every clip
            assertEquals(newKey.keyId, secondApi.vault()?.keyId)
            newKey.close()
        } finally {
            runCatching { second.api().logout() }
            runCatching { first.api().logout() }
        }
    }

    /** One phone: its own session store, cache and queue, over the test's shared HTTP client. */
    private inner class Device(name: String) {
        private val keys = InMemoryKeyProvider()
        val cache = HistoryCache(CryptoFile(File(tmp.newFolder(name), "cache.bin"), keys, KeyAliases.CACHE))
        val queue = OutboundQueue(CryptoFile(File(tmp.newFolder("$name-queue"), "queue.bin"), keys, KeyAliases.QUEUE), tmp.newFolder("$name-images"), keys)
        val thumbs = ThumbnailStore(tmp.newFolder("$name-thumbs"), keys)
        val manager = SessionManager(
            SessionStore(CryptoFile(File(tmp.newFolder("$name-session"), "session.bin"), keys, KeyAliases.SESSION)),
            cache, thumbs, queue, apiFactory = { u, token -> PrivatePasteApi(u, token, client) },
        )

        fun api() = PrivatePasteApi(baseUrl, checkNotNull(manager.session.value).token, client)

        fun engine(codec: ClipCodec): SyncEngine {
            val api = api()
            return SyncEngine(
                api = api, cache = cache, queue = queue, thumbs = thumbs,
                applier = ClipApplier { true }, flushQueue = { },
                links = OkHttpLinkFactory(open = { api.webSocket(it) }),
                codec = { codec }, ownDeviceId = checkNotNull(manager.session.value).deviceId,
                capture = LocalCaptureClock(), clock = Clock.systemUTC(), backoff = Backoff(), scope = scope,
            )
        }
    }

    // Half red, half blue: not a flat colour, so a corrupted chunk would show.
    private fun testPng(): ByteArray {
        val bitmap = Bitmap.createBitmap(IMAGE_WIDTH, IMAGE_HEIGHT, Bitmap.Config.ARGB_8888)
        for (y in 0 until IMAGE_HEIGHT) for (x in 0 until IMAGE_WIDTH) bitmap.setPixel(x, y, if (x < IMAGE_WIDTH / 2) Color.RED else Color.BLUE)
        return ByteArrayOutputStream().also { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }.toByteArray()
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
        const val IMAGE_WIDTH = 600
        const val IMAGE_HEIGHT = 300
        const val PASSPHRASE = "correct horse battery staple"
    }
}
