package xyz.ficdev.privatepaste.ui

import android.content.Intent
import kotlinx.coroutines.flow.StateFlow
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.clipboard.ClipboardWriter
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.SettingsStore
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.storage.ThumbnailStore
import xyz.ficdev.privatepaste.sync.SignInResult
import xyz.ficdev.privatepaste.sync.SyncState

/**
 * Everything the screens need from the rest of the app. Task 6's Application builds it from the session manager,
 * sync engine and stores; tests pass fakes. Read through [UiDepsProvider].
 */
interface UiDeps {
    /** The signed-in session, or null when signed out (also after a revocation). */
    val session: StateFlow<StoredSession?>

    suspend fun signIn(url: String, password: String, deviceName: String): SignInResult

    suspend fun signOut()

    /** Server of the previous session, to prefill the sign-in form after a sign-out or revocation. */
    val lastServerUrl: String?

    val syncState: StateFlow<SyncState>

    /** Null while signed out. */
    val cache: HistoryCache?

    /** Null while signed out. */
    val thumbs: ThumbnailStore?

    /** A client for the current session, or null while signed out. */
    fun api(): PrivatePasteApi?

    val clipboard: ClipboardWriter

    val settings: SettingsStore

    /** Starts the foreground sync service (Task 6 `SyncService.start`). Safe to call when it already runs. */
    val startService: () -> Unit

    val isBatteryExempt: () -> Boolean

    /** The `ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` intent for this package. */
    val requestBatteryExemption: () -> Intent

    val appVersion: String

    /** The full PNG of an image clip when this phone still has it locally (offline copy), else null. May read disk. */
    val cachedImage: (clipId: String) -> ByteArray? get() = { null }

    /** A UI call got 401 (this device was revoked): the same sign-out path as the engine's revoke. */
    fun onUnauthorized() = Unit
}

/** Implemented by the Application class. `MainActivity` reads `(application as UiDepsProvider).uiDeps`. */
interface UiDepsProvider {
    val uiDeps: UiDeps
}
