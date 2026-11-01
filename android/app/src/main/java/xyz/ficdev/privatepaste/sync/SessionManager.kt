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
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.api.UpdateRequiredException
import xyz.ficdev.privatepaste.core.ServerUrl
import xyz.ficdev.privatepaste.core.crypto.Handover
import xyz.ficdev.privatepaste.core.crypto.PassphraseWrap
import xyz.ficdev.privatepaste.core.crypto.VaultKey
import xyz.ficdev.privatepaste.core.crypto.WrongPassphraseException
import xyz.ficdev.privatepaste.core.model.HandoverCreated
import xyz.ficdev.privatepaste.core.model.VaultWrap
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.storage.SessionStore
import xyz.ficdev.privatepaste.storage.SettingsStore
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import xyz.ficdev.privatepaste.storage.VaultKeyStore
import xyz.ficdev.privatepaste.ui.UiText
import java.io.IOException
import java.security.GeneralSecurityException
import java.util.UUID
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
 * [vaultKeys]) and where the phone stands with the server's encryption ([vaultState]). [signIn], [signOut],
 * [onRevoked], [onKeyReset] and the vault key changes are serialized. Stop the sync engine (and the sender) before
 * [signOut], [onRevoked] or [onKeyReset], so nothing writes the cache after the clear. Blocking file IO runs on [io];
 * Argon2 runs on [cpu], never the main thread.
 *
 * The vault state machine ([checkVault]): a server without `/api/server-info` is [VaultState.SERVER_TOO_OLD]. Local
 * history, queue or thumbnails that weren't opened with the server's current key (from before encryption, or from
 * before a reset this phone missed) are wiped, with the stored key, before anything syncs. Then no vault is
 * [VaultState.NEEDS_OWNER_SETUP] for the owner and [VaultState.WAITING_FOR_OWNER] for others; a stored key with the
 * vault's key id is [VaultState.UNLOCKED], anything else [VaultState.LOCKED]. The copy returned by [unlock],
 * [unlockWithQr], [setUp] and [changePassphrase] is the same as the desktop's.
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
    /**
     * Called whenever local data is wiped (sign-in, sign-out, revoke, a key reset, a first-contact wipe), on [io] with
     * the lock held: the host clears what this manager doesn't own (plaintext image copies, the clip notification).
     */
    private val onLocalDataCleared: () -> Unit = {},
    private val io: CoroutineDispatcher = Dispatchers.IO,
    private val cpu: CoroutineDispatcher = Dispatchers.Default,
) {
    private val mutex = Mutex()
    private val _session = MutableStateFlow(store.load())
    private val _vaultKey = MutableStateFlow(vaultKeys?.load())
    private val _vaultState = MutableStateFlow(provisionalState())
    private val _vaultChecked = MutableStateFlow(false)
    private val _keyWasReset = MutableStateFlow(false)
    private val _isOwner = MutableStateFlow(false)

    /**
     * Where this phone stands with the server's encryption. At start, a phone that holds the key its history was
     * opened with shows as [VaultState.UNLOCKED] (the history works offline), but it syncs only once [vaultChecked].
     */
    val vaultState: StateFlow<VaultState> = _vaultState.asStateFlow()

    /** The server confirmed [vaultState] in this session (and stale local data is gone): syncing may start. */
    val vaultChecked: StateFlow<Boolean> = _vaultChecked.asStateFlow()

    /** The owner reset encryption while this phone held the old key: the unlock screen says so. */
    val keyWasReset: StateFlow<Boolean> = _keyWasReset.asStateFlow()

    /** Whether this account is the server's owner, as of the last [checkVault]. */
    val isOwner: StateFlow<Boolean> = _isOwner.asStateFlow()

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
        forgetVaultState()
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
            forgetVaultState()
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
        forgetVaultState()
        _session.value = null
    }

    /**
     * Unwraps the vault key from the server's [wrap] (Argon2, on [cpu]) and keeps it.
     * @throws xyz.ficdev.privatepaste.core.crypto.WrongPassphraseException when [passphrase] doesn't open it.
     * @throws java.security.GeneralSecurityException when the wrap is malformed.
     * @throws IOException when the key can't be saved.
     */
    suspend fun unlockVault(wrap: VaultWrap, passphrase: String) {
        useVaultKey(unwrap(wrap, passphrase))
    }

    /**
     * Saves [key] and makes it the current one; the one it replaces is closed. This manager owns [key] from the call
     * on: when it can't be kept (a failed save, or cancelled while waiting for the lock) it is closed.
     */
    suspend fun useVaultKey(key: VaultKey) {
        var kept = false
        try {
            mutex.withLock {
                withContext(io + NonCancellable) {
                    vaultKeys?.save(key)
                    replaceVaultKey(key)
                    kept = true
                }
            }
        } finally {
            if (!kept) key.close()
        }
    }

    /**
     * The owner reset encryption: the stored key opens nothing new, and the history it opened is gone from the server.
     * Forgets both; the session stays, and the phone is locked until it gets the new key.
     */
    suspend fun onKeyReset() = mutex.withLock {
        withContext(io + NonCancellable) { clearLocalData() }
        _keyWasReset.value = true
        _vaultState.value = VaultState.LOCKED
    }

    /** The server answered 426: syncing stopped until this app is updated. */
    fun onUpdateRequired() {
        if (_session.value != null) _vaultState.value = VaultState.UPDATE_REQUIRED
    }

    /**
     * Asks the server where encryption stands and settles [vaultState], wiping local data that belongs to another key
     * first (see the class doc). False when the server couldn't be reached (the state stays as it was).
     * Don't run it while the engine syncs: a [VaultState.UNLOCKED] phone whose check is done is left alone.
     * @throws ApiException.Unauthorized when this device was revoked.
     */
    suspend fun checkVault(): Boolean {
        val session = _session.value ?: return false
        val api = api(session) ?: return false
        try {
            if (api.serverInfo() == null) {
                settle(session, VaultState.SERVER_TOO_OLD)
                return true
            }
            val vault = api.vault()
            val owner = api.me().isOwner
            applyCheck(session, vault, owner)
            return true
        } catch (e: UpdateRequiredException) {
            settle(session, VaultState.UPDATE_REQUIRED)
            return true
        } catch (e: ApiException.Unauthorized) {
            throw e
        } catch (e: ApiException) {
            Log.w(TAG, "Cannot check the server's encryption (${e.javaClass.simpleName})")
            return false
        }
    }

    /** Unlocks with the passphrase. Null when unlocked (or the state moved on); otherwise the copy to show. */
    suspend fun unlock(passphrase: String): UiText? = shown {
        val session = _session.value ?: return@shown null
        val api = api(session) ?: return@shown null
        val wrap = ask(session, R.string.vault_unlock_failed) { api.vault() }
        if (wrap == null) {
            checkVault()   // the vault is gone: set up, or wait for the owner
            return@shown null
        }
        val key = try {
            unwrap(wrap, passphrase)
        } catch (e: WrongPassphraseException) {
            return@shown UiText.Res(R.string.vault_wrong_passphrase)
        } catch (e: GeneralSecurityException) {
            return@shown UiText.Res(R.string.vault_unsupported)
        }
        keep(session, key)
    }

    /** Unlocks with the text of a handover QR code (`Add a device` on another device). Null when unlocked. */
    suspend fun unlockWithQr(text: String): UiText? = shown {
        val qr = Handover.parseQr(text.trim()) ?: return@shown UiText.Res(R.string.vault_not_a_code)
        try {
            val session = _session.value ?: return@shown null
            val api = api(session) ?: return@shown null
            // Asked first, so a failure here doesn't spend the single-use code.
            val vault = ask(session, R.string.vault_unlock_failed) { api.vault() }
            if (vault == null) {
                checkVault()   // encryption isn't set up (any more): the state moves on
                return@shown null
            }
            // A code made before the owner reset encryption holds the old key: don't take it.
            if (vault.keyId != qr.keyId) return@shown UiText.Res(R.string.vault_code_expired)
            val blob = ask(session, R.string.vault_unlock_failed) { api.takeHandover(qr.id.toString()) }
                ?: return@shown UiText.Res(R.string.vault_code_expired)
            val key = try {
                Handover.unwrap(qr.otk, blob, qr.keyId)
            } catch (e: GeneralSecurityException) {
                return@shown UiText.Res(R.string.vault_not_a_code)
            }
            keep(session, key)
        } finally {
            qr.otk.fill(0)
        }
    }

    /** The owner sets up encryption with a new key. Null when done; otherwise the copy to show. */
    suspend fun setUp(passphrase: String, repeat: String): UiText? = shown {
        PassphraseWrap.problem(passphrase)?.let { return@shown UiText.Raw(it) }
        if (passphrase != repeat) return@shown UiText.Res(R.string.vault_mismatch)
        val session = _session.value ?: return@shown null
        val api = api(session) ?: return@shown null
        var owned: VaultKey? = VaultKey.create()
        try {
            val key = owned!!
            val wrap = withContext(cpu) { PassphraseWrap.wrap(key, passphrase) }
            try {
                ask(
                    session, R.string.vault_setup_failed,
                    forbidden = R.string.vault_waiting_for_owner, conflict = R.string.vault_set_up_elsewhere,
                ) { api.putVault(wrap) }
            } catch (e: Shown) {
                checkVault()   // set up meanwhile (409), or not the owner after all (403)
                throw e
            }
            owned = null
            keep(session, key)
        } finally {
            owned?.close()
        }
    }

    /**
     * The owner re-wraps the same key under [next], proving [current] first. Null when changed; otherwise the copy
     * to show.
     */
    suspend fun changePassphrase(current: String, next: String, repeat: String): UiText? = shown {
        PassphraseWrap.problem(next)?.let { return@shown UiText.Raw(it) }
        if (next != repeat) return@shown UiText.Res(R.string.vault_mismatch)
        val session = _session.value ?: return@shown null
        val api = api(session) ?: return@shown null
        val wrap = ask(session, R.string.vault_change_failed) { api.vault() } ?: return@shown UiText.Res(R.string.vault_change_failed)
        val key = try {
            unwrap(wrap, current)
        } catch (e: WrongPassphraseException) {
            return@shown UiText.Res(R.string.vault_wrong_passphrase)
        } catch (e: GeneralSecurityException) {
            return@shown UiText.Res(R.string.vault_unsupported)
        }
        key.use {
            val rewrapped = withContext(cpu) { PassphraseWrap.wrap(it, next) }
            ask(session, R.string.vault_change_failed, forbidden = R.string.vault_owner_only) { api.putVault(rewrapped) }
        }
        null
    }

    /**
     * Hands this phone's vault key to a new device: the server keeps the sealed key for 10 minutes, and the returned QR
     * text holds the one-time key that opens it. Never log the QR text.
     * @throws IllegalStateException when signed out or locked.
     * @throws ApiException when the server refuses or can't be reached.
     */
    suspend fun createHandover(): Pair<HandoverCreated, String> {
        val session = _session.value ?: throw IllegalStateException("Signed out")
        val api = api(session) ?: throw IllegalStateException("Signed out")
        val key = _vaultKey.value ?: throw IllegalStateException("Locked")
        val (otk, blob) = Handover.wrap(key)
        try {
            val created = api.createHandover(blob)
            return created to Handover.qrText(UUID.fromString(created.id), otk, key.keyId)
        } finally {
            otk.fill(0)
        }
    }

    /** Argon2 on [cpu]. A key unwrapped while the caller was being cancelled is closed, never leaked. */
    private suspend fun unwrap(wrap: VaultWrap, passphrase: String): VaultKey {
        var key: VaultKey? = null
        try {
            withContext(cpu) { key = PassphraseWrap.unwrap(wrap, passphrase) }
            return key!!.also { key = null }
        } finally {
            key?.close()
        }
    }

    /** Keeps [key] as the unlocked key of [session]. Owns [key]: it is closed when not kept. */
    private suspend fun keep(session: StoredSession, key: VaultKey): UiText? {
        var kept = false
        try {
            return mutex.withLock {
                if (_session.value !== session) return@withLock null   // signed out meanwhile
                try {
                    withContext(io + NonCancellable) {
                        if (hasLocalData() && cache.keyId != key.keyId) clearLocalData()
                        vaultKeys?.save(key)
                        replaceVaultKey(key)
                        kept = true
                        claimLocalData(key.keyId)
                    }
                } finally {
                    // Once the key is held, the state says so, whatever fails after that.
                    if (kept) {
                        _keyWasReset.value = false
                        _vaultChecked.value = true
                        _vaultState.value = VaultState.UNLOCKED
                    }
                }
                null
            }
        } catch (e: IOException) {
            Log.w(TAG, "Cannot save the vault key (${e.javaClass.simpleName})")
            return UiText.Res(R.string.vault_save_failed)
        } finally {
            if (!kept) key.close()
        }
    }

    private suspend fun applyCheck(session: StoredSession, vault: VaultWrap?, owner: Boolean) = mutex.withLock {
        if (_session.value !== session) return@withLock
        _isOwner.value = owner
        // Already unlocked and syncing: key changes reach the engine (reset message, foreign key id, 409).
        if (_vaultChecked.value && _vaultState.value == VaultState.UNLOCKED) return@withLock
        val serverKeyId = vault?.keyId
        withContext(io + NonCancellable) {
            if (hasLocalData() && (cache.keyId == null || cache.keyId != serverKeyId)) {
                Log.i(TAG, "Local history belongs to another key; clearing it")
                clearLocalData()
            }
            if (_vaultKey.value != null && _vaultKey.value?.keyId != serverKeyId) {
                vaultKeys?.delete()
                replaceVaultKey(null)
            }
            if (serverKeyId != null) claimLocalData(serverKeyId)
        }
        _vaultState.value = when {
            vault == null -> if (owner) VaultState.NEEDS_OWNER_SETUP else VaultState.WAITING_FOR_OWNER
            _vaultKey.value != null -> VaultState.UNLOCKED
            else -> VaultState.LOCKED
        }
        _vaultChecked.value = true
    }

    private suspend fun settle(session: StoredSession, state: VaultState) = mutex.withLock {
        if (_session.value !== session) return@withLock
        _vaultState.value = state
        _vaultChecked.value = true
    }

    /** What to show before the server is asked: a phone holding the key its history belongs to shows the history. */
    private fun provisionalState(): VaultState {
        val key = _vaultKey.value ?: return VaultState.UNKNOWN
        if (_session.value == null) return VaultState.UNKNOWN
        return if (cache.keyId == key.keyId || !hasLocalData()) VaultState.UNLOCKED else VaultState.UNKNOWN
    }

    private fun forgetVaultState() {
        _vaultState.value = VaultState.UNKNOWN
        _vaultChecked.value = false
        _keyWasReset.value = false
        _isOwner.value = false
    }

    private fun hasLocalData(): Boolean =
        cache.items.value.isNotEmpty() || cache.highestSeq > 0 || cache.lastAppliedAt != null ||
            !queue.isEmpty || !thumbs.isEmpty()

    // Marks what is held now (queued clips included) as belonging to [keyId], so a restart doesn't take it for stale.
    private fun claimLocalData(keyId: String) {
        if (cache.keyId == keyId) return
        cache.keyId = keyId
        cache.save()
    }

    private fun api(session: StoredSession): PrivatePasteApi? =
        session.serverUrl.toHttpUrlOrNull()?.let { apiFactory(it, session.token) }

    /** Copy to show, thrown out of a call by [ask] and caught by [shown]. */
    private class Shown(val text: UiText) : Exception()

    private inline fun shown(block: () -> UiText?): UiText? = try {
        block()
    } catch (e: Shown) {
        e.text
    }

    /**
     * Runs a server call, turning its failures into copy ([Shown]): [forbidden] for a 403 and [conflict] for a 409 when
     * given, else the action's generic [failed] line. The server's own text is never shown. A 401 propagates: the
     * device was revoked.
     */
    private suspend fun <T> ask(
        session: StoredSession,
        failed: Int,
        forbidden: Int? = null,
        conflict: Int? = null,
        call: suspend () -> T,
    ): T = try {
        call()
    } catch (e: UpdateRequiredException) {
        settle(session, VaultState.UPDATE_REQUIRED)
        throw Shown(UiText.Res(R.string.vault_update_required))
    } catch (e: ApiException.Unauthorized) {
        throw e
    } catch (e: ApiException.Rejected) {
        val copy = when {
            e.status == FORBIDDEN && forbidden != null -> forbidden
            e.status == CONFLICT && conflict != null -> conflict
            else -> failed
        }
        throw Shown(UiText.Res(copy))
    } catch (e: ApiException) {
        throw Shown(UiText.Res(R.string.signin_unreachable))
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
        onLocalDataCleared()
    }

    private companion object {
        const val TAG = "SessionManager"
        const val UNREACHABLE = "Can't reach the server. Check the address and your connection."
        val LOGOUT_TIMEOUT = 10.seconds
        const val FORBIDDEN = 403
        const val CONFLICT = 409
    }
}
