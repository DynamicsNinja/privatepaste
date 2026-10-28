package xyz.ficdev.privatepaste.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import xyz.ficdev.privatepaste.storage.DeliveryMode
import xyz.ficdev.privatepaste.storage.StoredSession

data class SettingsState(
    val serverUrl: String = "",
    val deviceName: String = "",
    val deliveryMode: DeliveryMode = DeliveryMode.AUTOMATIC,
    val batteryExempt: Boolean = false,
    val signingOut: Boolean = false,
    val appVersion: String = "",
)

/** Settings: read-only account details, the delivery mode, the battery exemption status and sign-out. */
class SettingsViewModel(
    session: StateFlow<StoredSession?>,
    private val readDeliveryMode: () -> DeliveryMode,
    private val writeDeliveryMode: (DeliveryMode) -> Unit,
    private val isBatteryExempt: () -> Boolean,
    private val performSignOut: suspend () -> Unit,
    appVersion: String,
) : ViewModel() {

    private val local = MutableStateFlow(
        SettingsState(deliveryMode = readDeliveryMode(), batteryExempt = isBatteryExempt(), appVersion = appVersion),
    )

    val state: StateFlow<SettingsState> = combine(session, local) { s, l ->
        l.copy(serverUrl = s?.serverUrl.orEmpty(), deviceName = s?.deviceName.orEmpty())
    }.stateIn(
        viewModelScope,
        SharingStarted.WhileSubscribed(5_000),
        local.value.copy(serverUrl = session.value?.serverUrl.orEmpty(), deviceName = session.value?.deviceName.orEmpty()),
    )

    fun setDeliveryMode(mode: DeliveryMode) {
        writeDeliveryMode(mode)
        local.value = local.value.copy(deliveryMode = readDeliveryMode())
    }

    /** Re-reads the battery exemption, e.g. when the screen resumes after the system dialog. */
    fun refresh() {
        local.value = local.value.copy(batteryExempt = isBatteryExempt())
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
