package xyz.ficdev.privatepaste

import android.content.ClipData
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import androidx.core.app.NotificationManagerCompat
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.clipboard.ClipImageFiles
import xyz.ficdev.privatepaste.core.FileNames
import xyz.ficdev.privatepaste.core.crypto.ClipCodec
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.HandoverCreated
import xyz.ficdev.privatepaste.core.model.ServerInfo
import xyz.ficdev.privatepaste.core.model.ServerLimits
import xyz.ficdev.privatepaste.files.ClipFileStore
import xyz.ficdev.privatepaste.send.ClipSender
import xyz.ficdev.privatepaste.service.BatteryOptimization
import xyz.ficdev.privatepaste.service.Notifications
import xyz.ficdev.privatepaste.service.ServiceClipApplier
import xyz.ficdev.privatepaste.service.SyncService
import xyz.ficdev.privatepaste.storage.CryptoFile
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.KeyAliases
import xyz.ficdev.privatepaste.storage.KeyProvider
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.storage.SessionStore
import xyz.ficdev.privatepaste.storage.SettingsStore
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import xyz.ficdev.privatepaste.storage.VaultKeyStore
import xyz.ficdev.privatepaste.sync.Backoff
import xyz.ficdev.privatepaste.sync.LocalCaptureClock
import xyz.ficdev.privatepaste.sync.OkHttpLinkFactory
import xyz.ficdev.privatepaste.sync.PrefsSignInHints
import xyz.ficdev.privatepaste.sync.SessionManager
import xyz.ficdev.privatepaste.sync.SignInResult
import xyz.ficdev.privatepaste.sync.SyncEngine
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.sync.VaultState
import xyz.ficdev.privatepaste.ui.UiDeps
import xyz.ficdev.privatepaste.ui.UiText
import java.io.File
import java.io.IOException
import java.security.GeneralSecurityException
import java.time.Clock
import java.util.concurrent.atomic.AtomicInteger

/**
 * The app's object graph: singletons for the process (stores, clipboard, one OkHttpClient, the session manager)
 * plus a [SessionGraph] (API client, sender, engine) built per session and torn down on sign-out or revoke.
 *
 * Engine lifecycle: [SyncService] alone starts ([startEngine]) and stops ([stopEngine]) the current session's
 * engine, and the engine runs only once the session's vault check settled on unlocked ([SessionManager.vaultChecked]),
 * so stale local data is wiped before the first sync. Sign-in, sign-out and revoke are serialized by [lifecycle].
 */
@OptIn(ExperimentalCoroutinesApi::class)
class AppGraph(private val app: PrivatePasteApp, keys: KeyProvider, private val appScope: CoroutineScope) {

    private val dir: File = app.filesDir
    private val client = OkHttpClient()
    private val clock: Clock = Clock.systemUTC()

    val settings = SettingsStore(app)
    val imageFiles = ClipImageFiles(File(app.cacheDir, "clipboard"))   // served by the FileProvider (file_paths.xml)
    val clipFiles = ClipFileStore(File(app.cacheDir, "files"))          // served by the FileProvider too
    private val outgoing = File(app.cacheDir, "outgoing")               // shared files copied for sending (ClipSender)
    private val saver = app.downloadsSaver()
    val clipboard = app.clipboardWriter(imageFiles)
    val cache = HistoryCache(CryptoFile(File(dir, "history.bin"), keys, KeyAliases.CACHE))
    val thumbs = ThumbnailStore(File(dir, "thumbnails"), keys)
    val queue = OutboundQueue(CryptoFile(File(dir, "queue.bin"), keys, KeyAliases.QUEUE), File(dir, "queue-images"), keys)
    val sessions = SessionManager(
        store = SessionStore(CryptoFile(File(dir, "session.bin"), keys, KeyAliases.SESSION)),
        cache = cache,
        thumbs = thumbs,
        queue = queue,
        apiFactory = ::api,
        hints = PrefsSignInHints(app),
        settings = settings,
        vaultKeys = VaultKeyStore(CryptoFile(File(dir, "vault.bin"), keys, KeyAliases.VAULT)),
        onLocalDataCleared = ::clearPlaintextCopies,
    )

    /**
     * Every wipe of local data takes these too: plaintext PNG copies of clips, the opened files of file clips, the
     * copies of shared files a cancelled share left in [outgoing], and the clip preview notification.
     */
    private fun clearPlaintextCopies() {
        deletePlaintextFiles()
        Notifications.cancelClip(app)
    }

    private fun deletePlaintextFiles() {
        imageFiles.clear()
        clipFiles.clear()
        outgoing.deleteRecursively()
    }

    private val lifecycle = Mutex()

    /** Between [startEngine] and [stopEngine]: the service wants the engine running once the vault allows it. */
    @Volatile private var serviceWantsSync = false

    /** The engine may run: the vault is unlocked and the server confirmed it this session. */
    private val syncAllowed: Flow<Boolean> =
        combine(sessions.vaultState, sessions.vaultChecked) { state, checked -> state == VaultState.UNLOCKED && checked }
            .distinctUntilChanged()

    private fun syncAllowedNow() = sessions.vaultState.value == VaultState.UNLOCKED && sessions.vaultChecked.value

    private val signedOutAlertCount = AtomicInteger()

    /** How many times a revoke was handled (and its alert posted). Test hook for "exactly once". */
    internal val signedOutAlerts: Int get() = signedOutAlertCount.get()
    private val _current = MutableStateFlow<SessionGraph?>(null)

    /** The current session's graph; null while signed out. */
    val current: StateFlow<SessionGraph?> = _current.asStateFlow()

    val isSignedIn: Boolean get() = sessions.session.value != null

    /** The current session's [SessionGraph.displayState]; SIGNED_OUT without a session. For the notification. */
    val connectionStates: Flow<SyncState> = _current.flatMapLatest { it?.displayState ?: flowOf(SyncState.SIGNED_OUT) }

    /** [connectionStates] as a StateFlow, for the UI. */
    val syncState: StateFlow<SyncState> = connectionStates.stateIn(appScope, SharingStarted.Eagerly, SyncState.SIGNED_OUT)

    /** The state to show right now, read synchronously (the first foreground notification). */
    fun connectionStateNow(): SyncState = _current.value?.displayStateNow() ?: SyncState.SIGNED_OUT

    init {
        sessions.session.value?.let { _current.value = SessionGraph(it) }   // process start while signed in
        // A file clip's opened files live as long as the clip is in the history.
        appScope.launch(Dispatchers.IO) {
            cache.items.map { items -> items.mapTo(HashSet()) { it.id } }.distinctUntilChanged().collect { ids ->
                try {
                    clipFiles.prune(ids)
                } catch (e: SecurityException) {
                    Log.w(TAG, "Cannot prune the clip files (${e.javaClass.simpleName})")
                }
            }
        }
    }

    private fun api(baseUrl: HttpUrl, token: String?) = PrivatePasteApi(baseUrl, token, client)

    /** Seals and opens clips with the vault key held right now; null while locked. Read it per use, never keep it. */
    private fun codec(): ClipCodec? = sessions.vaultKey.value?.let(::ClipCodec)

    /** Everything one session needs. A revoked engine stays signed out, so each session gets a new one. */
    inner class SessionGraph(val session: StoredSession) {
        val api: PrivatePasteApi = api(session.serverUrl.toHttpUrl(), session.token)
        private val capture = LocalCaptureClock()
        val sender = ClipSender(
            api, ::codec, ::limits, queue, cache, capture, app::notifyUser, clock,
            onKeyReset = ::keyWasReset, isEnding = { ending }, kinds = ::kinds, outgoing = outgoing,
        )

        @Volatile private var serverInfo: ServerInfo? = null

        /** What the server takes, asked once per session. A server without `/api/server-info` gets the defaults. */
        private suspend fun info(): ServerInfo = serverInfo ?: (api.serverInfo() ?: LEGACY_INFO).also { serverInfo = it }

        private suspend fun limits(): ServerLimits = info().limits

        private suspend fun kinds(): List<String> = info().kinds

        /** The full PNG of an image clip, fetched and opened. */
        suspend fun image(id: String): ByteArray {
            val codec = codec() ?: throw IOException("No vault key")
            val sealed = api.blob(id, 0)
            return withContext(Dispatchers.Default) { codec.decodeBlob(sealed) }
        }

        /**
         * The server refused the key [keyId] as reset (the engine or the sender says which key it used), so a key set
         * up after that is never thrown away.
         */
        private fun keyWasReset(keyId: String) {
            appScope.launch { onKeyReset(this@SessionGraph, keyId) }   // not in [scope]: the handling stops the engine
        }

        /** The engine's scope: IO for its file writes; a child of the app scope. */
        private val scope = CoroutineScope(SupervisorJob(appScope.coroutineContext.job) + Dispatchers.IO)

        val engine = SyncEngine(
            api = api,
            cache = cache,
            queue = queue,
            thumbs = thumbs,
            applier = ServiceClipApplier(app, { settings.deliveryMode }, clipboard, imageFiles, ::image),
            flushQueue = sender::flushQueue,
            links = OkHttpLinkFactory(api::webSocket),
            codec = ::codec,
            ownDeviceId = session.deviceId,
            capture = capture,
            clock = clock,
            backoff = Backoff(),
            scope = scope,
        )

        /**
         * Set once sign-out or revoke begins: the engine must not be started again, and a send still in flight must
         * not write its result (see [markEnding]).
         */
        @Volatile var ending = false
            private set

        /** Sets [ending] under the sender's commit lock: no live send is half-way through writing once this returns. */
        fun markEnding() = synchronized(sender.commitLock) { ending = true }

        /**
         * A new engine reports OFFLINE until its loop runs. Until it has tried once (left OFFLINE), that reads as
         * CONNECTING, so a fresh start never shows "Offline, retrying".
         */
        private val attempted = MutableStateFlow(false)

        val displayState: Flow<SyncState> =
            combine(engine.state, attempted, sessions.vaultState) { s, tried, vault -> display(s, tried, vault) }

        fun displayStateNow(): SyncState = display(engine.state.value, attempted.value, sessions.vaultState.value)

        // Waiting for the passphrase or the owner: nothing connects until someone opens the app.
        private fun display(s: SyncState, tried: Boolean, vault: VaultState) = when {
            vault == VaultState.LOCKED || vault == VaultState.WAITING_FOR_OWNER -> SyncState.LOCKED
            s == SyncState.OFFLINE && !tried -> SyncState.CONNECTING
            else -> s
        }

        /** Wakes [checkVaultLoop]; declared before [init], which starts the loop. */
        private val vaultWake = Channel<Unit>(Channel.CONFLATED)

        init {
            scope.launch {
                engine.state.first { it != SyncState.OFFLINE }
                attempted.value = true
            }
            scope.launch {
                engine.revoked.first()
                appScope.launch { onRevoked(this@SessionGraph) }   // not in [scope]: the teardown cancels it
            }
            scope.launch { engine.keyReset.collect { keyWasReset(it) } }
            scope.launch {
                engine.updateRequired.collect {
                    Log.i(TAG, "The server needs a newer app")
                    sessions.onUpdateRequired()
                }
            }
            scope.launch { checkVaultLoop() }
            scope.launch {
                syncAllowed.collect { allowed -> if (allowed && serviceWantsSync && !ending) engine.start() }
            }
        }

        /** Asks the server about the vault again now (Try again, a network change), unless this session syncs already. */
        fun recheckVault() {
            vaultWake.trySend(Unit)
        }

        /**
         * Settles the vault state with the server, retrying with backoff while it can't be reached, then waits for
         * [recheckVault]. A revoke found here takes the engine's path.
         */
        private suspend fun checkVaultLoop() {
            val backoff = Backoff()
            while (true) {
                val reached = try {
                    syncAllowedNow() || sessions.checkVault()
                } catch (e: ApiException.Unauthorized) {
                    appScope.launch { onRevoked(this@SessionGraph) }
                    return
                }
                if (reached) {
                    backoff.reset()
                    vaultWake.receive()
                } else {
                    withTimeoutOrNull(backoff.next()) { vaultWake.receive() }
                }
            }
        }

        fun close() = scope.cancel()
    }

    /** Called by [SyncService]. False when signed out (or the session is ending): the service then stops. */
    fun startEngine(): Boolean {
        val g = _current.value ?: return false
        if (g.ending) return false
        serviceWantsSync = true
        if (syncAllowedNow()) g.engine.start()   // else the session graph starts it once the vault check allows
        return true
    }

    /**
     * Called when [SyncService] is destroyed. The stop takes the engine's run synchronously (undispatched), so a
     * start that follows right after waits for this stop instead of being cancelled by it.
     */
    fun stopEngine() {
        serviceWantsSync = false
        val g = _current.value ?: return
        appScope.launch(start = CoroutineStart.UNDISPATCHED) { g.engine.stop() }
    }

    suspend fun signIn(url: String, username: String, password: String, deviceName: String): SignInResult = lifecycle.withLock {
        // The UI signs in only while signed out; a leftover graph would write the cache the sign-in clears.
        _current.value?.let { end(it) }
        val result = sessions.signIn(url, username, password, deviceName)
        if (result is SignInResult.Ok) {
            sessions.session.value?.let { _current.value = SessionGraph(it) }
            Notifications.cancelSignedOut(app)
        }
        result
    }

    /** Stop the engine, sign out (clears local data), tear down the session, stop the service. */
    suspend fun signOut() = withContext(NonCancellable) {
        lifecycle.withLock {
            val g = _current.value
            if (g != null) {
                g.markEnding()
                g.engine.stop()
            }
            sessions.signOut()
            if (g != null) end(g)   // the service sees the session gone and stops itself (by start id)
        }
    }

    private suspend fun onRevoked(g: SessionGraph) = withContext(NonCancellable) {
        lifecycle.withLock {
            if (_current.value !== g) return@withLock   // already signed out or replaced
            g.markEnding()
            g.engine.stop()   // already stopped by the revoke; waits for its final save
            sessions.onRevoked()
            Notifications.postSignedOut(app)
            signedOutAlertCount.incrementAndGet()
            end(g)
            Log.i(TAG, "This device was signed out by the server")
        }
    }

    /**
     * The owner reset encryption: stop syncing, and forget the old key and the history it opened (the server wiped
     * it). The session stays; syncing resumes once the phone has the new key. [keyId] is the key that was reset: a
     * signal that arrives after another key was set up is ignored.
     */
    private suspend fun onKeyReset(g: SessionGraph, keyId: String) = withContext(NonCancellable) {
        lifecycle.withLock {
            if (_current.value !== g || g.ending) return@withLock
            if (sessions.vaultKey.value?.keyId != keyId) return@withLock
            g.engine.stop()
            sessions.onKeyReset()   // also clears the plaintext copies (clearPlaintextCopies)
            Log.i(TAG, "Encryption was reset; waiting for the new key")
        }
    }

    // Caller holds lifecycle.
    private suspend fun end(g: SessionGraph) {
        g.markEnding()
        g.engine.stop()
        withContext(Dispatchers.IO) { deletePlaintextFiles() }   // plaintext copies of clips: gone with the session
        Notifications.cancelClip(app)   // a clip preview (maybe a password) is gone with the session too
        if (_current.value === g) _current.value = null
        g.close()
    }

    /** The Copy action: puts a cached clip on the clipboard. False when unknown or the write failed. */
    suspend fun copyClip(id: String): Boolean {
        val g = _current.value
        val clip = cache.items.value.firstOrNull { it.id == id } ?: return false
        return try {
            when (clip.kind) {
                ClipKind.TEXT, ClipKind.LINK -> clipboard.writeText(clip.text ?: return false)
                ClipKind.IMAGE -> {
                    val png = withContext(Dispatchers.IO) { imageFiles.find(id)?.readBytes() }
                        ?: g?.image(id)
                        ?: return false
                    withContext(Dispatchers.IO) { clipboard.writeImage(id, png) }
                }
                ClipKind.FILE -> {
                    val files = openedFiles(id) ?: return false
                    withContext(Dispatchers.IO) { clipboard.writeFiles(files) }
                }
                ClipKind.UNKNOWN -> return false  // a kind from a newer client: nothing this build can copy
            }
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: ApiException.Unauthorized) {
            unauthorized(g)
            false
        } catch (e: ApiException) {
            Log.w(TAG, "Cannot fetch the image to copy (${e.javaClass.simpleName})")
            false
        } catch (e: IOException) {
            Log.w(TAG, "Cannot copy the clip (${e.javaClass.simpleName})")
            false
        } catch (e: GeneralSecurityException) {
            Log.w(TAG, "Cannot open the image to copy (${e.javaClass.simpleName})")
            false
        }
    }

    /** The Save action: a cached file clip's files, downloaded if needed, into Downloads. False on any failure. */
    suspend fun saveClipFiles(id: String): Boolean {
        val files = openedFiles(id) ?: return false
        return withContext(Dispatchers.IO) { saver.save(files) }
    }

    /**
     * The Share action: a chooser for a cached file clip's files (downloaded if needed), readable by the app the user
     * picks through this app's FileProvider. Null when it isn't a file clip or the files can't be had.
     */
    suspend fun shareClipFiles(id: String): Intent? {
        val files = openedFiles(id) ?: return null
        val uris = files.map(clipboard::contentUri)
        val send = if (uris.size == 1) {
            Intent(Intent.ACTION_SEND).setType(FileNames.mimeFor(files[0].name)).putExtra(Intent.EXTRA_STREAM, uris[0])
        } else {
            Intent(Intent.ACTION_SEND_MULTIPLE).setType("*/*").putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris))
        }
        // The grant covers the URIs in the ClipData; the chooser carries both on to the app the user picks.
        send.clipData = ClipData.newRawUri(null, uris[0]).apply { uris.drop(1).forEach { addItem(ClipData.Item(it)) } }
        send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        return Intent.createChooser(send, null)
    }

    /**
     * The opened files of the cached file clip [id] (ClipFileStore), fetched from the server the first time. Null when
     * the clip is unknown or not a file clip, locked, offline, or a blob doesn't open; a 401 takes the revoke path.
     */
    private suspend fun openedFiles(id: String): List<File>? {
        val clip = cache.items.value.firstOrNull { it.id == id }?.takeIf { it.kind == ClipKind.FILE } ?: return null
        val infos = clip.files?.takeIf { it.isNotEmpty() } ?: return null
        val g = _current.value
        val codec = codec() ?: return null
        return try {
            clipFiles.materialize(clip, { n -> (g ?: throw IOException("Signed out")).api.blob(id, n, infos[n].size) }, codec)
        } catch (e: CancellationException) {
            throw e
        } catch (e: ApiException.Unauthorized) {
            unauthorized(g)
            null
        } catch (e: Exception) {
            // ApiException (offline), IOException, GeneralSecurityException (a blob that doesn't open), a closed key.
            Log.w(TAG, "Cannot get the clip's files (${e.javaClass.simpleName})")
            null
        }
    }

    /** A call outside the engine got 401: the device was revoked. Same path as the engine's revoke. */
    private fun unauthorized(g: SessionGraph?) {
        if (g != null) appScope.launch { onRevoked(g) }
    }

    /** The full PNG of an image clip when it is still in the clipboard files (offline copy from the history). */
    private fun cachedImage(id: String): ByteArray? = try {
        imageFiles.find(id)?.readBytes()
    } catch (e: IOException) {
        null
    }

    val uiDeps: UiDeps = object : UiDeps {
        override val session = sessions.session
        override suspend fun signIn(url: String, username: String, password: String, deviceName: String) =
            this@AppGraph.signIn(url, username, password, deviceName)
        override suspend fun signOut() = this@AppGraph.signOut()
        override val lastServerUrl: String? get() = sessions.lastServerUrl
        override val lastDeviceName: String? get() = sessions.lastDeviceName
        override val lastUsername: String? get() = sessions.lastUsername
        override val syncState: StateFlow<SyncState> = this@AppGraph.syncState
        override val cache: HistoryCache? get() = this@AppGraph.cache.takeIf { _current.value != null }
        override val thumbs: ThumbnailStore? get() = this@AppGraph.thumbs.takeIf { _current.value != null }
        override fun api(): PrivatePasteApi? = _current.value?.api
        override fun codec(): ClipCodec? = this@AppGraph.codec().takeIf { _current.value != null }
        override val clipboard = this@AppGraph.clipboard
        override val settings = this@AppGraph.settings
        override val startService: () -> Unit = { SyncService.start(app) }
        override val isBatteryExempt: () -> Boolean = { BatteryOptimization.isExempt(app) }
        override val areNotificationsEnabled: () -> Boolean = { NotificationManagerCompat.from(app).areNotificationsEnabled() }
        override val requestBatteryExemption: () -> Intent = { BatteryOptimization.requestIntent(app) }
        override val appVersion: String = versionName()
        override val cachedImage: (String) -> ByteArray? = ::cachedImage
        override fun onUnauthorized() = unauthorized(_current.value)
        override val vaultState: StateFlow<VaultState> = sessions.vaultState
        override val keyWasReset: StateFlow<Boolean> = sessions.keyWasReset
        override val isOwner: StateFlow<Boolean> = sessions.isOwner
        override suspend fun unlock(passphrase: String) = vaultCall { sessions.unlock(passphrase) }
        override suspend fun unlockWithQr(text: String) = vaultCall { sessions.unlockWithQr(text) }
        override suspend fun setUp(passphrase: String, repeat: String) = vaultCall { sessions.setUp(passphrase, repeat) }
        override suspend fun changePassphrase(current: String, next: String, repeat: String) =
            vaultCall { sessions.changePassphrase(current, next, repeat) }
        override suspend fun createHandover(): Pair<HandoverCreated, String> = try {
            sessions.createHandover()
        } catch (e: ApiException.Unauthorized) {
            unauthorized(_current.value)
            throw e
        }
        override fun recheckVault() {
            _current.value?.recheckVault()
        }
    }

    /** A vault action from the UI; a 401 takes the revoke path and shows nothing. */
    private suspend fun vaultCall(block: suspend () -> UiText?): UiText? = try {
        block()
    } catch (e: ApiException.Unauthorized) {
        unauthorized(_current.value)
        null
    }

    private fun versionName(): String = try {
        val info = if (Build.VERSION.SDK_INT >= 33) {
            app.packageManager.getPackageInfo(app.packageName, PackageManager.PackageInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION")
            app.packageManager.getPackageInfo(app.packageName, 0)
        }
        info.versionName.orEmpty()
    } catch (e: PackageManager.NameNotFoundException) {
        ""
    }

    private companion object {
        const val TAG = "AppGraph"

        /** The server's defaults (PP_MAX_TEXT_KB, PP_MAX_IMAGE_MB, PP_MAX_FILE_MB). */
        val DEFAULT_LIMITS = ServerLimits(textKb = 1024, imageMb = 20, fileMb = 25)

        /** A server without `/api/server-info` (protocol 1): the defaults, and no file clips. */
        val LEGACY_INFO = ServerInfo(protocol = 1, kinds = listOf("text", "link", "image"), limits = DEFAULT_LIMITS)
    }
}
