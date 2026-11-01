package xyz.ficdev.privatepaste.ui

import android.content.Intent
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.core.crypto.ClipCodec
import xyz.ficdev.privatepaste.clipboard.ClipboardWriter
import xyz.ficdev.privatepaste.core.model.HandoverCreated
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.SettingsStore
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import xyz.ficdev.privatepaste.sync.SignInResult
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.sync.VaultState

/**
 * Everything the screens need from the rest of the app. Task 6's Application builds it from the session manager,
 * sync engine and stores; tests pass fakes. Read through [UiDepsProvider].
 */
interface UiDeps {
    /** The signed-in session, or null when signed out (also after a revocation). */
    val session: StateFlow<StoredSession?>

    suspend fun signIn(url: String, username: String, password: String, deviceName: String): SignInResult

    suspend fun signOut()

    /** Server of the previous session, to prefill the sign-in form after a sign-out or revocation. */
    val lastServerUrl: String?

    /** Device name of the previous session, to prefill the sign-in form; null falls back to the model name. */
    val lastDeviceName: String? get() = null

    /** Name of the last sign-in, to prefill the sign-in form. */
    val lastUsername: String? get() = null

    val syncState: StateFlow<SyncState>

    /** Null while signed out. */
    val cache: HistoryCache?

    /** Null while signed out. */
    val thumbs: ThumbnailStore?

    /** A client for the current session, or null while signed out. */
    fun api(): PrivatePasteApi?

    /** Opens and seals clips with this phone's vault key; null while signed out or locked. */
    fun codec(): ClipCodec? = null

    val clipboard: ClipboardWriter

    val settings: SettingsStore

    /** Starts the foreground sync service (Task 6 `SyncService.start`). Safe to call when it already runs. */
    val startService: () -> Unit

    val isBatteryExempt: () -> Boolean

    /** Whether this app may show notifications at all (the app switch, and the permission on Android 13+). */
    val areNotificationsEnabled: () -> Boolean get() = { true }

    /** The `ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` intent for this package. */
    val requestBatteryExemption: () -> Intent

    val appVersion: String

    /** The full PNG of an image clip when this phone still has it locally (offline copy), else null. May read disk. */
    val cachedImage: (clipId: String) -> ByteArray? get() = { null }

    /** A UI call got 401 (this device was revoked): the same sign-out path as the engine's revoke. */
    fun onUnauthorized() = Unit

    /** Where this phone stands with the server's encryption; anything but UNLOCKED shows the unlock screen. */
    val vaultState: StateFlow<VaultState> get() = UNLOCKED

    /** The owner reset encryption: the unlock screen asks for the new passphrase. */
    val keyWasReset: StateFlow<Boolean> get() = NOT_SET

    /** Whether this account is the server's owner (setup, Change passphrase). */
    val isOwner: StateFlow<Boolean> get() = NOT_SET

    /** Each returns null when done, otherwise the copy to show (see `SessionManager`). */
    suspend fun unlock(passphrase: String): UiText? = null
    suspend fun unlockWithQr(text: String): UiText? = null
    suspend fun setUp(passphrase: String, repeat: String): UiText? = null
    suspend fun changePassphrase(current: String, next: String, repeat: String): UiText? = null

    /** A one-time handover of the vault key for `Add a device`, and its QR text. Throws when it can't be made. */
    suspend fun createHandover(): Pair<HandoverCreated, String> = throw IllegalStateException("No vault key")

    /** Asks the server about the vault again (Try again). */
    fun recheckVault() = Unit
}

// Defaults for hosts without encryption (test fakes): always unlocked.
private val UNLOCKED: StateFlow<VaultState> = MutableStateFlow(VaultState.UNLOCKED)
private val NOT_SET: StateFlow<Boolean> = MutableStateFlow(false)

/** Implemented by the Application class. `MainActivity` reads `(application as UiDepsProvider).uiDeps`. */
interface UiDepsProvider {
    val uiDeps: UiDeps
}
