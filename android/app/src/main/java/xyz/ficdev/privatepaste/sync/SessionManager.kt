package xyz.ficdev.privatepaste.sync

import android.content.Context
import android.util.Log
import androidx.core.content.edit
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.core.ServerUrl
import xyz.ficdev.privatepaste.core.crypto.PassphraseWrap
import xyz.ficdev.privatepaste.core.crypto.VaultKey
import xyz.ficdev.privatepaste.core.model.VaultWrap
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.storage.SessionStore
import xyz.ficdev.privatepaste.storage.SettingsStore
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import xyz.ficdev.privatepaste.storage.VaultKeyStore
import java.io.IOException
import kotlin.time.Duration.Companion.seconds

/** What the sign-in form is pre-filled with after a revoke. Not secret: plain storage is fine. */
interface SignInHints {
    var serverUrl: String?
    var deviceName: String?
}

/** Process-lifetime hints (tests, or a host that does not persist them). */
class InMemorySignInHints : SignInHints {
    @Volatile override var serverUrl: String? = null
    @Volatile override var deviceName: String? = null
}

/** Hints in plain SharedPreferences "sign_in_hints", so a revoke survives a process restart. */
class PrefsSignInHints(context: Context) : SignInHints {
    private val prefs = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    override var serverUrl: String?
        get() = prefs.getString(KEY_URL, null)
        set(value) = prefs.edit { if (value == null) remove(KEY_URL) else putString(KEY_URL, value) }

    override var deviceName: String?
        get() = prefs.getString(KEY_DEVICE, null)
        set(value) = prefs.edit { if (value == null) remove(KEY_DEVICE) else putString(KEY_DEVICE, value) }

    private companion object {
        const val NAME = "sign_in_hints"
        const val KEY_URL = "serverUrl"
        const val KEY_DEVICE = "deviceName"
    }
}

/**
 * Signs in and out, and owns the local data tied to a session, including the vault key ([vaultKey], kept in
 * [vaultKeys]). [signIn], [signOut], [onRevoked], [onKeyReset] and the vault key changes are serialized. Stop the sync
 * engine (and the sender) before [signOut], [onRevoked] or [onKeyReset], so nothing writes the cache after the clear.
 * Blocking file IO runs on [io]; Argon2 runs on [cpu], never the main thread.
 */
class SessionManager(
    private val store: SessionStore,
    private val cache: HistoryCache,
    private val thumbs: ThumbnailStore,
    private val queue: OutboundQueue,
    private val apiFactory: (baseUrl: HttpUrl, token: String?) -> PrivatePasteApi,
    private val hints: SignInHints = InMemorySignInHints(),
    private val settings: SettingsStore? = null,
    private val vaultKeys: VaultKeyStore? = null,
    private val io: CoroutineDispatcher = Dispatchers.IO,
    private val cpu: CoroutineDispatcher = Dispatchers.Default,
) {
    private val mutex = Mutex()
    private val _session = MutableStateFlow(store.load())
    private val _vaultKey = MutableStateFlow(vaultKeys?.load())

    /**
     * The vault key this phone holds, or null (locked, or encryption not set up). Owned here: a replaced or forgotten
     * key is closed, so read it when needed instead of keeping it.
     */
    val vaultKey: StateFlow<VaultKey?> = _vaultKey.asStateFlow()

    /** The signed-in session, or null when signed out. */
    val session: StateFlow<StoredSession?> = _session.asStateFlow()

    /** For the sign-in form: the current session's server, else the one of the session that was revoked. */
    val lastServerUrl: String? get() = _session.value?.serverUrl ?: hints.serverUrl

    /** For the sign-in form: the current session's device name, else the one of the session that was revoked. */
    val lastDeviceName: String? get() = _session.value?.deviceName ?: hints.deviceName

    /** For the sign-in form: the name of the last successful sign-in. */
    val lastUsername: String? get() = settings?.lastUsername

    suspend fun signIn(url: String, username: String, password: String, deviceName: String): SignInResult = mutex.withLock {
        val baseUrl = try {
            ServerUrl.normalize(url)
        } catch (e: IllegalArgumentException) {
            return SignInResult.InvalidUrl(e.message ?: "That doesn't look like a server address.")
        }
        val login = try {
            apiFactory(baseUrl, null).login(username, password, deviceName)
        } catch (e: ApiException.Unauthorized) {
            return SignInResult.WrongPassword
        } catch (e: ApiException.RateLimited) {
            return SignInResult.RateLimited
        } catch (e: ApiException.Transient) {
            return SignInResult.Unreachable(UNREACHABLE)
        } catch (e: ApiException.Rejected) {
            return SignInResult.Failed(e.message ?: "Sign-in failed.")
        }
        val session = StoredSession(baseUrl.toString(), login.deviceId, deviceName, login.token)
        try {
            withContext(io + NonCancellable) {
                clearLocalData()   // another account's (or a revoked session's) history must not show
                store.save(session)
            }
        } catch (e: IOException) {
            Log.w(TAG, "Cannot save the session (${e.javaClass.simpleName})")
            return SignInResult.Failed("Couldn't save the sign-in on this phone. Try again.")
        }
        hints.serverUrl = session.serverUrl
        hints.deviceName = deviceName
        settings?.lastUsername = username
        _session.value = session
        SignInResult.Ok
    }

    /** Best-effort logout call (at most [LOGOUT_TIMEOUT]), then clears everything local, also when cancelled. */
    suspend fun signOut() = mutex.withLock {
        val current = _session.value
        try {
            val baseUrl = current?.serverUrl?.toHttpUrlOrNull()
            if (current != null && baseUrl != null) {
                withTimeoutOrNull(LOGOUT_TIMEOUT) { apiFactory(baseUrl, current.token).logout() }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.w(TAG, "Logout call failed (${e.javaClass.simpleName}); signing out locally")
        } finally {
            withContext(io + NonCancellable) {
                store.clear()
                clearLocalData()
                hints.serverUrl = null
                hints.deviceName = null
            }
            _session.value = null
        }
    }

    /** The server revoked this device: forget the token and local data, keep the server URL and name for the form. */
    suspend fun onRevoked() = mutex.withLock {
        val current = _session.value
        withContext(io + NonCancellable) {
            if (current != null) {
                hints.serverUrl = current.serverUrl
                hints.deviceName = current.deviceName
            }
            store.clear()
            clearLocalData()
        }
        _session.value = null
    }

    /**
     * Unwraps the vault key from the server's [wrap] (Argon2, on [cpu]) and keeps it.
     * @throws xyz.ficdev.privatepaste.core.crypto.WrongPassphraseException when [passphrase] doesn't open it.
     * @throws java.security.GeneralSecurityException when the wrap is malformed.
     * @throws IOException when the key can't be saved.
     */
    suspend fun unlockVault(wrap: VaultWrap, passphrase: String) {
        val key = withContext(cpu) { PassphraseWrap.unwrap(wrap, passphrase) }
        useVaultKey(key)
    }

    /** Saves [key] and makes it the current one (this manager owns it from now on); the one it replaces is closed. */
    suspend fun useVaultKey(key: VaultKey) = mutex.withLock {
        try {
            withContext(io + NonCancellable) { vaultKeys?.save(key) }
        } catch (e: IOException) {
            key.close()
            throw e
        }
        replaceVaultKey(key)
    }

    /**
     * The owner reset encryption: the stored key opens nothing new, and the history it opened is gone from the server.
     * Forgets both; the session stays.
     */
    suspend fun onKeyReset() = mutex.withLock {
        withContext(io + NonCancellable) { clearLocalData() }
    }

    // Caller holds mutex.
    private fun replaceVaultKey(key: VaultKey?) {
        val old = _vaultKey.value
        _vaultKey.value = key
        if (old !== key) old?.close()
    }

    // Caller holds mutex. Includes the vault key: it belongs to the session's server.
    private fun clearLocalData() {
        cache.clear()
        thumbs.clear()
        queue.clear()
        vaultKeys?.delete()
        replaceVaultKey(null)
    }

    private companion object {
        const val TAG = "SessionManager"
        const val UNREACHABLE = "Can't reach the server. Check the address and your connection."
        val LOGOUT_TIMEOUT = 10.seconds
    }
}
