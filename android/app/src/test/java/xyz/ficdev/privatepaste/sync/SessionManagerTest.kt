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
import xyz.ficdev.privatepaste.storage.VaultKeyStore
import xyz.ficdev.privatepaste.core.crypto.PassphraseWrap
import xyz.ficdev.privatepaste.core.crypto.VaultKey
import xyz.ficdev.privatepaste.core.crypto.VaultKeyClosedException
import kotlinx.coroutines.launch
import xyz.ficdev.privatepaste.core.crypto.WrongPassphraseException
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertFalse
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.RecordedRequest
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.core.model.ProtocolJson
import xyz.ficdev.privatepaste.core.model.VaultKdf
import xyz.ficdev.privatepaste.core.model.VaultWrap
import xyz.ficdev.privatepaste.ui.UiText
import java.util.Base64
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
    private lateinit var vault: VaultKeyStore
    private lateinit var url: String

    @Before
    fun setUp() {
        server.start(InetAddress.getByName("127.0.0.1"), 0)
        url = "http://127.0.0.1:${server.port}/"
        store = SessionStore(CryptoFile(File(tmp.root, "session.bin"), keys, KeyAliases.SESSION))
        cache = HistoryCache(CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE))
        queue = OutboundQueue(CryptoFile(File(tmp.root, "queue.bin"), keys, KeyAliases.QUEUE), tmp.newFolder("queue-images"), keys)
        thumbs = ThumbnailStore(tmp.newFolder("thumbs"), keys)
        vault = VaultKeyStore(CryptoFile(File(tmp.root, "vault.bin"), keys, KeyAliases.VAULT))
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
        vaultKeys = vault,
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
        vault.save(VaultKey.create())
    }

    private fun assertLocalDataCleared() {
        assertTrue(cache.items.value.isEmpty())
        assertNull(cache.lastAppliedAt)
        assertEquals(0L, cache.highestSeq)
        assertTrue(queue.isEmpty)
        assertNull(thumbs.get("hash-old"))
        val reloaded = HistoryCache(CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE))
        assertTrue(reloaded.items.value.isEmpty())
        assertNull("the vault key is deleted", vault.load())
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

    @Test
    fun signOutDeletesVaultKey(): Unit = runBlocking {
        val m = manager()
        server.enqueue(json("""{"deviceId":"dev-1","token":"tok-1"}"""))
        assertEquals(SignInResult.Ok, m.signIn(url, "Ana", "dev", "Pixel 9"))
        val key = VaultKey.create()
        m.useVaultKey(key)
        assertEquals(key.keyId, m.vaultKey.value!!.keyId)
        assertEquals(key.keyId, vault.load()!!.keyId)
        server.enqueue(MockResponse().setResponseCode(204))

        m.signOut()

        assertNull(m.vaultKey.value)
        assertNull(vault.load())
        assertThrows("the key in memory is wiped", IllegalStateException::class.java) { key.export() }
    }

    @Test
    fun onRevokedDeletesVaultKey(): Unit = runBlocking {
        val m = manager()
        m.useVaultKey(VaultKey.create())
        m.onRevoked()
        assertNull(m.vaultKey.value)
        assertNull(vault.load())
    }

    @Test
    fun theStoredKeyIsLoadedOnStartAndAReplacedOneIsClosed(): Unit = runBlocking {
        val first = VaultKey.create()
        vault.save(first)
        val m = manager()
        val loaded = m.vaultKey.value!!
        assertEquals(first.keyId, loaded.keyId)

        val second = VaultKey.create()
        m.useVaultKey(second)
        assertEquals(second.keyId, vault.load()!!.keyId)
        assertThrows(IllegalStateException::class.java) { loaded.export() }
    }

    @Test
    fun keyResetForgetsTheKeyAndTheLocalData(): Unit = runBlocking {
        val m = manager()
        server.enqueue(json("""{"deviceId":"dev-1","token":"tok-1"}"""))
        assertEquals(SignInResult.Ok, m.signIn(url, "Ana", "dev", "Pixel 9"))
        seedLocalData()
        m.useVaultKey(VaultKey.create())

        m.onKeyReset()

        assertNotNull("still signed in", m.session.value)
        assertNull(m.vaultKey.value)
        assertLocalDataCleared()
    }

    @Test
    fun unlockVaultUnwrapsAndStoresTheKey(): Unit = runBlocking {
        val key = VaultKey.create()
        val wrap = PassphraseWrap.wrap(key, "correct horse battery")
        val m = manager()
        assertThrows(WrongPassphraseException::class.java) { runBlocking { m.unlockVault(wrap, "wrong horse battery") } }
        assertNull(m.vaultKey.value)

        m.unlockVault(wrap, "correct horse battery")
        assertEquals(key.keyId, m.vaultKey.value!!.keyId)
        assertEquals(key.keyId, vault.load()!!.keyId)
    }

    @Test
    fun aKeyThatCannotBeKeptIsClosed(): Unit = runBlocking {
        val m = manager()
        server.enqueue(json("""{"deviceId":"dev-1","token":"tok-1"}"""))
        assertEquals(SignInResult.Ok, m.signIn(url, "Ana", "dev", "Pixel 9"))
        // A slow logout holds the manager's lock while the key waits for it.
        server.enqueue(MockResponse().setResponseCode(204).setHeadersDelay(2, java.util.concurrent.TimeUnit.SECONDS))
        val signOut = launch(kotlinx.coroutines.Dispatchers.Default) { m.signOut() }
        server.takeRequest(5, java.util.concurrent.TimeUnit.SECONDS)   // the login
        server.takeRequest(5, java.util.concurrent.TimeUnit.SECONDS)   // the logout, now in flight

        val key = VaultKey.create()
        val waiting = launch(kotlinx.coroutines.Dispatchers.Default) { m.useVaultKey(key) }
        kotlinx.coroutines.delay(200)
        waiting.cancel()
        waiting.join()
        signOut.join()

        assertThrows("the abandoned key is wiped", VaultKeyClosedException::class.java) { key.export() }
        assertNull(m.vaultKey.value)
        assertNull(vault.load())
    }

    // The vault state machine. The wrap's contents don't matter to the check, only its key id.

    private fun wrapFor(keyId: String) = VaultWrap(
        keyId = keyId,
        kdf = VaultKdf("argon2id", 65536, 3, 1, Base64.getEncoder().encodeToString(ByteArray(16))),
        nonce = Base64.getEncoder().encodeToString(ByteArray(12)),
        wrappedKey = Base64.getEncoder().encodeToString(ByteArray(48)),
    )

    /** Answers the check's three calls; anything else (a sync, an upload) fails the test through [unexpected]. */
    private val unexpected = mutableListOf<String>()

    private fun vaultServer(wrap: VaultWrap?, owner: Boolean = false, serverInfo: Boolean = true) {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.requestUrl?.encodedPath) {
                "/api/server-info" -> if (serverInfo) {
                    json("""{"protocol":2,"kinds":["text","link","image"],"limits":{"textKb":1024,"imageMb":20}}""")
                } else {
                    MockResponse().setResponseCode(404)
                }
                "/api/vault" -> if (wrap == null) {
                    MockResponse().setResponseCode(404)
                } else {
                    json(ProtocolJson.json.encodeToString(VaultWrap.serializer(), wrap))
                }
                "/api/me" -> json("""{"name":"Ana","isOwner":$owner}""")
                else -> MockResponse().setResponseCode(500).also { unexpected += request.path.orEmpty() }
            }
        }
    }

    private fun signedIn(): SessionManager {
        store.save(StoredSession(url, "dev-1", "Pixel 9", "tok-1"))
        return manager()
    }

    @Test
    fun preE2eCacheWipedBeforeFirstSync(): Unit = runBlocking {
        seedLocalData()   // the way the app before encryption left it: no key id on the cache
        vault.delete()
        assertNull(cache.keyId)
        val m = signedIn()
        assertEquals(VaultState.UNKNOWN, m.vaultState.value)
        assertFalse("nothing syncs before the check", m.vaultChecked.value)
        vaultServer(wrapFor("0123456789abcdef"))

        assertTrue(m.checkVault())

        assertLocalDataCleared()
        assertEquals(VaultState.LOCKED, m.vaultState.value)
        assertTrue(m.vaultChecked.value)
        assertEquals("the empty cache now belongs to the server's key", "0123456789abcdef", cache.keyId)
        assertEquals(emptyList<String>(), unexpected)
    }

    @Test
    fun aPhoneOfflineAcrossAResetForgetsTheOldKeyAndHistory(): Unit = runBlocking {
        seedLocalData()
        val old = VaultKey.create()
        vault.save(old)
        cache.keyId = old.keyId
        cache.save()
        val m = signedIn()
        assertEquals("the history shows offline with the key it belongs to", VaultState.UNLOCKED, m.vaultState.value)
        assertFalse(m.vaultChecked.value)
        val held = m.vaultKey.value!!
        vaultServer(wrapFor("fedcba9876543210"))   // the owner reset encryption while this phone was away

        assertTrue(m.checkVault())

        assertLocalDataCleared()
        assertNull(m.vaultKey.value)
        assertEquals(VaultState.LOCKED, m.vaultState.value)
        assertThrows("the old key is wiped in memory too", VaultKeyClosedException::class.java) { held.export() }
    }

    @Test
    fun theKeyItsHistoryBelongsToUnlocksAndKeepsIt(): Unit = runBlocking {
        seedLocalData()
        val key = VaultKey.create()
        vault.save(key)
        cache.keyId = key.keyId
        cache.save()
        val m = signedIn()
        vaultServer(wrapFor(key.keyId))

        assertTrue(m.checkVault())

        assertEquals(VaultState.UNLOCKED, m.vaultState.value)
        assertTrue(m.vaultChecked.value)
        assertEquals(1, cache.items.value.size)
        assertFalse(queue.isEmpty)
        assertEquals(key.keyId, m.vaultKey.value!!.keyId)
    }

    @Test
    fun noVaultIsSetupForTheOwnerAndWaitingForOthers(): Unit = runBlocking {
        val m = signedIn()
        vaultServer(wrap = null, owner = true)
        assertTrue(m.checkVault())
        assertEquals(VaultState.NEEDS_OWNER_SETUP, m.vaultState.value)
        assertTrue(m.isOwner.value)

        vaultServer(wrap = null, owner = false)
        assertTrue(m.checkVault())
        assertEquals(VaultState.WAITING_FOR_OWNER, m.vaultState.value)
        assertFalse(m.isOwner.value)
    }

    @Test
    fun oldServerIsServerTooOld(): Unit = runBlocking {
        val m = signedIn()
        vaultServer(wrap = null, serverInfo = false)
        assertTrue(m.checkVault())
        assertEquals(VaultState.SERVER_TOO_OLD, m.vaultState.value)
    }

    @Test
    fun anUnreachableServerLeavesTheStateAlone(): Unit = runBlocking {
        val m = signedIn()
        server.shutdown()
        assertFalse(m.checkVault())
        assertEquals(VaultState.UNKNOWN, m.vaultState.value)
        assertFalse(m.vaultChecked.value)
    }

    @Test
    fun aKeyResetLocksWithTheResetNotice(): Unit = runBlocking {
        val m = signedIn()
        m.useVaultKey(VaultKey.create())
        m.onKeyReset()
        assertEquals(VaultState.LOCKED, m.vaultState.value)
        assertTrue(m.keyWasReset.value)
    }

    @Test
    fun setUpKeepsTheNewKeyAndUnlocks(): Unit = runBlocking {
        val m = signedIn()
        var put: String? = null
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                put = request.body.readUtf8()
                return MockResponse().setResponseCode(201)
            }
        }
        assertNull(m.setUp("a long enough passphrase", "a long enough passphrase"))
        assertEquals(VaultState.UNLOCKED, m.vaultState.value)
        val key = m.vaultKey.value!!
        assertEquals(key.keyId, vault.load()!!.keyId)
        assertEquals(key.keyId, cache.keyId)
        assertTrue(put!!.contains("\"keyId\":\"${key.keyId}\""))
        // The wrap on the server opens with the passphrase.
        val wrap = ProtocolJson.json.decodeFromString(VaultWrap.serializer(), put)
        assertEquals(key.keyId, PassphraseWrap.unwrap(wrap, "a long enough passphrase").keyId)
    }

    @Test
    fun changePassphraseRewrapsTheSameKey(): Unit = runBlocking {
        val key = VaultKey.create()
        val wrap = PassphraseWrap.wrap(key, "the old passphrase")
        val m = signedIn()
        var put: String? = null
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.method) {
                "GET" -> json(ProtocolJson.json.encodeToString(VaultWrap.serializer(), wrap))
                else -> MockResponse().setResponseCode(200).also { put = request.body.readUtf8() }
            }
        }
        assertEquals(
            UiText.Res(R.string.vault_wrong_passphrase),
            m.changePassphrase("not the old one", "the new passphrase", "the new passphrase"),
        )
        assertNull(put)
        assertEquals(UiText.Res(R.string.vault_mismatch), m.changePassphrase("the old passphrase", "the new passphrase", "the new passphrasf"))
        assertEquals(UiText.Raw(PassphraseWrap.RULE_MESSAGE), m.changePassphrase("the old passphrase", "short", "short"))

        assertNull(m.changePassphrase("the old passphrase", "the new passphrase", "the new passphrase"))
        val rewrapped = ProtocolJson.json.decodeFromString(VaultWrap.serializer(), put!!)
        assertEquals(key.keyId, rewrapped.keyId)
        assertEquals(key.keyId, PassphraseWrap.unwrap(rewrapped, "the new passphrase").keyId)
    }
}
