package xyz.ficdev.privatepaste

import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.clipboard.ClipImageFiles
import xyz.ficdev.privatepaste.core.model.ClipKind
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
import xyz.ficdev.privatepaste.sync.Backoff
import xyz.ficdev.privatepaste.sync.LocalCaptureClock
import xyz.ficdev.privatepaste.sync.OkHttpLinkFactory
import xyz.ficdev.privatepaste.sync.PrefsSignInHints
import xyz.ficdev.privatepaste.sync.SessionManager
import xyz.ficdev.privatepaste.sync.SignInResult
import xyz.ficdev.privatepaste.sync.SyncEngine
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.ui.UiDeps
import java.io.File
import java.io.IOException
import java.time.Clock

/**
 * The app's object graph: singletons for the process (stores, clipboard, one OkHttpClient, the session manager)
 * plus a [SessionGraph] (API client, sender, engine) built per session and torn down on sign-out or revoke.
 *
 * Engine lifecycle: [SyncService] alone starts ([startEngine]) and stops ([stopEngine]) the current session's
 * engine. Sign-in, sign-out and revoke are serialized by [lifecycle].
 */
@OptIn(ExperimentalCoroutinesApi::class)
class AppGraph(private val app: PrivatePasteApp, keys: KeyProvider, private val appScope: CoroutineScope) {

    private val dir: File = app.filesDir
    private val client = OkHttpClient()
    private val clock: Clock = Clock.systemUTC()

    val settings = SettingsStore(app)
    val imageFiles = ClipImageFiles(File(app.cacheDir, "clipboard"))   // served by the FileProvider (file_paths.xml)
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
    )

    private val lifecycle = Mutex()
    private val _current = MutableStateFlow<SessionGraph?>(null)

    /** The current session's graph; null while signed out. */
    val current: StateFlow<SessionGraph?> = _current.asStateFlow()

    val isSignedIn: Boolean get() = sessions.session.value != null

    /** The current engine's state; SIGNED_OUT without a session. */
    val syncState: StateFlow<SyncState> = _current
        .flatMapLatest { it?.engine?.state ?: flowOf(SyncState.SIGNED_OUT) }
        .stateIn(appScope, SharingStarted.Eagerly, SyncState.SIGNED_OUT)

    init {
        sessions.session.value?.let { _current.value = SessionGraph(it) }   // process start while signed in
    }

    private fun api(baseUrl: HttpUrl, token: String?) = PrivatePasteApi(baseUrl, token, client)

    /** Everything one session needs. A revoked engine stays signed out, so each session gets a new one. */
    inner class SessionGraph(val session: StoredSession) {
        val api: PrivatePasteApi = api(session.serverUrl.toHttpUrl(), session.token)
        private val capture = LocalCaptureClock()
        val sender = ClipSender(api, queue, cache, capture, app::notifyUser, clock)

        /** The engine's scope: IO for its file writes; a child of the app scope. */
        private val scope = CoroutineScope(SupervisorJob(appScope.coroutineContext.job) + Dispatchers.IO)

        val engine = SyncEngine(
            api = api,
            cache = cache,
            queue = queue,
            thumbs = thumbs,
            applier = ServiceClipApplier(app, { settings.deliveryMode }, clipboard, imageFiles, api::image),
            flushQueue = sender::flushQueue,
            links = OkHttpLinkFactory(api::webSocket),
            ownDeviceId = session.deviceId,
            capture = capture,
            clock = clock,
            backoff = Backoff(),
            scope = scope,
        )

        /** Set once sign-out or revoke begins: the engine must not be started again. */
        @Volatile var ending = false

        init {
            scope.launch {
                engine.revoked.first()
                appScope.launch { onRevoked(this@SessionGraph) }   // not in [scope]: the teardown cancels it
            }
        }

        fun close() = scope.cancel()
    }

    /** Called by [SyncService]. False when signed out (or the session is ending): the service then stops. */
    fun startEngine(): Boolean {
        val g = _current.value ?: return false
        if (g.ending) return false
        g.engine.start()
        return true
    }

    /**
     * Called when [SyncService] is destroyed. The stop takes the engine's run synchronously (undispatched), so a
     * start that follows right after waits for this stop instead of being cancelled by it.
     */
    fun stopEngine() {
        val g = _current.value ?: return
        appScope.launch(start = CoroutineStart.UNDISPATCHED) { g.engine.stop() }
    }

    suspend fun signIn(url: String, password: String, deviceName: String): SignInResult = lifecycle.withLock {
        // The UI signs in only while signed out; a leftover graph would write the cache the sign-in clears.
        _current.value?.let { end(it) }
        val result = sessions.signIn(url, password, deviceName)
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
                g.ending = true
                g.engine.stop()
            }
            sessions.signOut()
            if (g != null) end(g)
            SyncService.stop(app)
        }
    }

    private suspend fun onRevoked(g: SessionGraph) = withContext(NonCancellable) {
        lifecycle.withLock {
            if (_current.value !== g) return@withLock   // already signed out or replaced
            g.ending = true
            g.engine.stop()   // already stopped by the revoke; waits for its final save
            sessions.onRevoked()
            Notifications.postSignedOut(app)
            end(g)
            SyncService.stop(app)
            Log.i(TAG, "This device was signed out by the server")
        }
    }

    // Caller holds lifecycle.
    private suspend fun end(g: SessionGraph) {
        g.ending = true
        g.engine.stop()
        if (_current.value === g) _current.value = null
        g.close()
    }

    /** The Copy action: puts a cached clip on the clipboard. False when unknown or the write failed. */
    suspend fun copyClip(id: String): Boolean {
        val clip = cache.items.value.firstOrNull { it.id == id } ?: return false
        return try {
            when (clip.kind) {
                ClipKind.TEXT, ClipKind.LINK -> clipboard.writeText(clip.text ?: return false)
                ClipKind.IMAGE -> {
                    val png = withContext(Dispatchers.IO) { imageFiles.find(id)?.readBytes() }
                        ?: _current.value?.api?.image(id)
                        ?: return false
                    withContext(Dispatchers.IO) { clipboard.writeImage(id, png) }
                }
            }
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: ApiException) {
            Log.w(TAG, "Cannot fetch the image to copy (${e.javaClass.simpleName})")
            false
        } catch (e: IOException) {
            Log.w(TAG, "Cannot copy the clip (${e.javaClass.simpleName})")
            false
        }
    }

    /** The full PNG of an image clip when it is still in the clipboard files (offline copy from the history). */
    private fun cachedImage(id: String): ByteArray? = try {
        imageFiles.find(id)?.readBytes()
    } catch (e: IOException) {
        null
    }

    val uiDeps: UiDeps = object : UiDeps {
        override val session = sessions.session
        override suspend fun signIn(url: String, password: String, deviceName: String) =
            this@AppGraph.signIn(url, password, deviceName)
        override suspend fun signOut() = this@AppGraph.signOut()
        override val lastServerUrl: String? get() = sessions.lastServerUrl
        override val syncState: StateFlow<SyncState> = this@AppGraph.syncState
        override val cache: HistoryCache? get() = this@AppGraph.cache.takeIf { _current.value != null }
        override val thumbs: ThumbnailStore? get() = this@AppGraph.thumbs.takeIf { _current.value != null }
        override fun api(): PrivatePasteApi? = _current.value?.api
        override val clipboard = this@AppGraph.clipboard
        override val settings = this@AppGraph.settings
        override val startService: () -> Unit = { SyncService.start(app) }
        override val isBatteryExempt: () -> Boolean = { BatteryOptimization.isExempt(app) }
        override val requestBatteryExemption: () -> Intent = { BatteryOptimization.requestIntent(app) }
        override val appVersion: String = versionName()
        override val cachedImage: (String) -> ByteArray? = ::cachedImage
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
    }
}
