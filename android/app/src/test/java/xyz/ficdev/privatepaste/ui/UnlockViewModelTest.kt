package xyz.ficdev.privatepaste.ui

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withTimeout
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.core.crypto.Handover
import xyz.ficdev.privatepaste.core.crypto.PassphraseWrap
import xyz.ficdev.privatepaste.core.crypto.VaultKey
import xyz.ficdev.privatepaste.core.model.ProtocolJson
import xyz.ficdev.privatepaste.core.model.VaultWrap
import xyz.ficdev.privatepaste.storage.CryptoFile
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.InMemoryKeyProvider
import xyz.ficdev.privatepaste.storage.KeyAliases
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.storage.SessionStore
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import xyz.ficdev.privatepaste.storage.VaultKeyStore
import xyz.ficdev.privatepaste.sync.SessionManager
import xyz.ficdev.privatepaste.sync.VaultState
import xyz.ficdev.privatepaste.ui.unlock.UnlockState
import xyz.ficdev.privatepaste.ui.unlock.UnlockViewModel
import java.io.File
import java.net.InetAddress
import java.util.UUID

/** The unlock screen's copy, end to end: the view model over a real [SessionManager] and a fake server. */
@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
class UnlockViewModelTest {

    @get:Rule val tmp = TemporaryFolder()

    private val keys = InMemoryKeyProvider()
    private val server = MockWebServer()
    private lateinit var sessions: SessionManager

    @Before
    fun setUp() {
        Dispatchers.setMain(UnconfinedTestDispatcher())
        server.start(InetAddress.getByName("127.0.0.1"), 0)
        val store = SessionStore(CryptoFile(File(tmp.root, "session.bin"), keys, KeyAliases.SESSION))
        store.save(StoredSession("http://127.0.0.1:${server.port}/", "dev-1", "Pixel 9", "tok-1"))
        val client = OkHttpClient()
        sessions = SessionManager(
            store = store,
            cache = HistoryCache(CryptoFile(File(tmp.root, "cache.bin"), keys, KeyAliases.CACHE)),
            thumbs = ThumbnailStore(tmp.newFolder("thumbs"), keys),
            queue = OutboundQueue(CryptoFile(File(tmp.root, "queue.bin"), keys, KeyAliases.QUEUE), tmp.newFolder("queue-images"), keys),
            apiFactory = { baseUrl, token -> PrivatePasteApi(baseUrl, token, client) },
            vaultKeys = VaultKeyStore(CryptoFile(File(tmp.root, "vault.bin"), keys, KeyAliases.VAULT)),
        )
    }

    @After
    fun tearDown() {
        server.shutdown()
        Dispatchers.resetMain()
    }

    private fun vm() = UnlockViewModel(
        vaultState = sessions.vaultState,
        keyWasReset = sessions.keyWasReset,
        unlock = sessions::unlock,
        unlockWithQr = sessions::unlockWithQr,
        setUp = sessions::setUp,
        recheck = {},
        performSignOut = {},
    )

    /** The form once the action finished. */
    private fun UnlockViewModel.settled(): UnlockState = runBlocking {
        withTimeout(60_000) { state.first { !it.busy } }
    }

    private fun json(body: String) = MockResponse().setHeader("Content-Type", "application/json").setBody(body)

    @Test
    fun wrongPassphraseShowsTheCopy() {
        val wrap = PassphraseWrap.wrap(VaultKey.create(), "correct horse battery staple")
        server.enqueue(json(ProtocolJson.json.encodeToString(VaultWrap.serializer(), wrap)))
        val vm = vm()

        vm.onPassphraseChange("wrong horse battery staple")
        vm.unlock()

        val state = vm.settled()
        assertEquals(UiText.Res(R.string.vault_wrong_passphrase), state.error)
        assertNull(sessions.vaultKey.value)
    }

    @Test
    fun aWrapWithUnsupportedSettingsSaysSoInsteadOfWrongPassphrase() {
        val wrap = PassphraseWrap.wrap(VaultKey.create(), "correct horse battery staple")
        val greedy = wrap.copy(kdf = wrap.kdf.copy(memoryKiB = 4 * 1024 * 1024))   // outside the client's bounds
        server.enqueue(json(ProtocolJson.json.encodeToString(VaultWrap.serializer(), greedy)))
        val vm = vm()

        vm.onPassphraseChange("correct horse battery staple")
        vm.unlock()

        assertEquals(UiText.Res(R.string.vault_unsupported), vm.settled().error)
    }

    @Test
    fun theRightPassphraseUnlocks() {
        val key = VaultKey.create()
        server.enqueue(json(ProtocolJson.json.encodeToString(VaultWrap.serializer(), PassphraseWrap.wrap(key, "correct horse battery staple"))))
        val vm = vm()

        vm.onPassphraseChange("correct horse battery staple")
        vm.unlock()

        val state = vm.settled()
        assertNull(state.error)
        assertEquals("", state.passphrase)   // not kept once used
        assertEquals(VaultState.UNLOCKED, sessions.vaultState.value)
        assertEquals(key.keyId, sessions.vaultKey.value!!.keyId)
    }

    @Test
    fun aCodeThatIsNotAHandoverShowsTheCopy() {
        val vm = vm()
        vm.onScanned("https://example.com/not-a-handover")
        assertEquals(UiText.Res(R.string.vault_not_a_code), vm.settled().error)
        assertEquals(0, server.requestCount)
    }

    @Test
    fun anExpiredOrUsedCodeShowsTheCopy() {
        server.enqueue(MockResponse().setResponseCode(404))
        val vm = vm()
        val id = UUID.randomUUID()
        vm.onScanned(Handover.qrText(id, ByteArray(32) { 7 }, "0123456789abcdef"))

        assertEquals(UiText.Res(R.string.vault_code_expired), vm.settled().error)
        assertEquals("/api/vault/handovers/$id", server.takeRequest().path)
    }

    @Test
    fun aScannedHandoverUnlocks() {
        val key = VaultKey.create()
        val (otk, blob) = Handover.wrap(key)
        val id = UUID.randomUUID()
        server.enqueue(json("""{"blob":"${java.util.Base64.getEncoder().encodeToString(blob)}"}"""))
        server.enqueue(json(ProtocolJson.json.encodeToString(VaultWrap.serializer(), PassphraseWrap.wrap(key, "correct horse battery staple"))))
        val vm = vm()

        vm.onScanned(Handover.qrText(id, otk, key.keyId))

        assertNull(vm.settled().error)
        assertEquals(VaultState.UNLOCKED, sessions.vaultState.value)
        assertEquals(key.keyId, sessions.vaultKey.value!!.keyId)
    }

    @Test
    fun mismatchedSetupRepeatShowsTheCopy() {
        val vm = vm()
        vm.onPassphraseChange("a long enough passphrase")
        vm.onRepeatChange("a long enough passphrasf")
        vm.setUp()
        assertEquals(UiText.Res(R.string.vault_mismatch), vm.settled().error)
        assertEquals(0, server.requestCount)
    }

    @Test
    fun aShortSetupPassphraseShowsTheRule() {
        val vm = vm()
        vm.onPassphraseChange("short")
        vm.onRepeatChange("short")
        vm.setUp()
        assertEquals(UiText.Raw("Use at least 12 characters. A short sentence works well."), vm.settled().error)
    }
}
