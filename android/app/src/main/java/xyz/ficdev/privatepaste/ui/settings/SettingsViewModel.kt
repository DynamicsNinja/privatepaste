package xyz.ficdev.privatepaste.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.core.model.HandoverCreated
import xyz.ficdev.privatepaste.storage.DeliveryMode
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.ui.UiText
import java.time.Instant

data class SettingsState(
    val serverUrl: String = "",
    val deviceName: String = "",
    val deliveryMode: DeliveryMode = DeliveryMode.AUTOMATIC,
    val batteryExempt: Boolean = false,
    val signingOut: Boolean = false,
    val appVersion: String = "",
    /** Delivery by notification while the app's notifications are off: clips would never show up. */
    val notificationsOff: Boolean = false,
    /** Only the owner may change the passphrase. */
    val isOwner: Boolean = false,
    val changingPassphrase: Boolean = false,
    /** Why the last change failed, shown in the change dialog. */
    val passphraseError: UiText? = null,
    /** The last change went through: "Passphrase changed." */
    val passphraseChanged: Boolean = false,
    /** The open `Add a device` screen; null when closed. */
    val addDevice: AddDeviceState? = null,
)

/** A handover code being made ([loading]), shown ([qrText] until [expiresAt]) or failed ([error]). */
data class AddDeviceState(
    val loading: Boolean = true,
    val qrText: String? = null,
    val expiresAt: Instant? = null,
    val error: UiText? = null,
)

/**
 * Settings: read-only account details, the delivery mode, the battery exemption status, encryption (the owner's
 * `Change passphrase`, and `Add a device`) and sign-out.
 */
class SettingsViewModel(
    session: StateFlow<StoredSession?>,
    private val readDeliveryMode: () -> DeliveryMode,
    private val writeDeliveryMode: (DeliveryMode) -> Unit,
    private val isBatteryExempt: () -> Boolean,
    private val performSignOut: suspend () -> Unit,
    appVersion: String,
    private val areNotificationsEnabled: () -> Boolean = { true },
    private val isOwner: StateFlow<Boolean> = MutableStateFlow(false),
    private val performChangePassphrase: suspend (current: String, next: String, repeat: String) -> UiText? = { _, _, _ -> null },
    private val createHandover: suspend () -> Pair<HandoverCreated, String> = { throw IllegalStateException("No vault key") },
) : ViewModel() {

    private val local = MutableStateFlow(
        readDeliveryMode().let { mode ->
            SettingsState(
                deliveryMode = mode,
                batteryExempt = isBatteryExempt(),
                appVersion = appVersion,
                notificationsOff = notificationsOff(mode),
            )
        },
    )
    private var handoverJob: Job? = null

    val state: StateFlow<SettingsState> = combine(session, local, isOwner) { s, l, owner ->
        l.copy(serverUrl = s?.serverUrl.orEmpty(), deviceName = s?.deviceName.orEmpty(), isOwner = owner)
    }.stateIn(
        viewModelScope,
        SharingStarted.WhileSubscribed(5_000),
        local.value.copy(
            serverUrl = session.value?.serverUrl.orEmpty(),
            deviceName = session.value?.deviceName.orEmpty(),
            isOwner = isOwner.value,
        ),
    )

    fun setDeliveryMode(mode: DeliveryMode) {
        writeDeliveryMode(mode)
        val now = readDeliveryMode()
        local.value = local.value.copy(deliveryMode = now, notificationsOff = notificationsOff(now))
    }

    /** Re-reads the battery exemption and the notification switch, e.g. when the screen resumes after a system screen. */
    fun refresh() {
        local.value = local.value.copy(
            batteryExempt = isBatteryExempt(),
            notificationsOff = notificationsOff(local.value.deliveryMode),
        )
    }

    private fun notificationsOff(mode: DeliveryMode) = mode == DeliveryMode.NOTIFICATION && !areNotificationsEnabled()

    /** The owner re-wraps the vault key under a new passphrase. Others can't (the server refuses them too). */
    fun changePassphrase(current: String, next: String, repeat: String) {
        if (!isOwner.value || local.value.changingPassphrase) return
        local.update { it.copy(changingPassphrase = true, passphraseError = null, passphraseChanged = false) }
        viewModelScope.launch {
            val error = try {
                performChangePassphrase(current, next, repeat)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                UiText.Res(R.string.vault_change_failed)
            }
            local.update { it.copy(changingPassphrase = false, passphraseError = error, passphraseChanged = error == null) }
        }
    }

    /** The change dialog opened: forget the last result. */
    fun clearPassphraseResult() = local.update { it.copy(passphraseError = null, passphraseChanged = false) }

    /** Opens `Add a device` with a new one-time code (also "Show a new code"). */
    fun addDevice() {
        handoverJob?.cancel()
        local.update { it.copy(addDevice = AddDeviceState()) }
        handoverJob = viewModelScope.launch {
            val shown = try {
                val (created, qr) = createHandover()
                AddDeviceState(loading = false, qrText = qr, expiresAt = created.expiresAt)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                AddDeviceState(loading = false, error = UiText.Res(R.string.add_device_failed))
            }
            local.update { if (it.addDevice == null) it else it.copy(addDevice = shown) }
        }
    }

    /** The code's 10 minutes are up: forget its text (it holds the one-time key) and keep the screen open. */
    fun expireAddDevice() = local.update { s ->
        s.copy(addDevice = s.addDevice?.copy(qrText = null, expiresAt = null))
    }

    fun closeAddDevice() {
        handoverJob?.cancel()
        local.update { it.copy(addDevice = null) }
    }

    fun signOut() {
        if (local.value.signingOut) return
        local.value = local.value.copy(signingOut = true)
        viewModelScope.launch {
            try {
                performSignOut()
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                // The session manager clears local state even when the server call fails; nothing to show here.
            } finally {
                local.value = local.value.copy(signingOut = false)
            }
        }
    }
}
