package xyz.ficdev.privatepaste.ui.unlock

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.sync.VaultState
import xyz.ficdev.privatepaste.ui.UiText

data class UnlockState(
    val vault: VaultState = VaultState.UNKNOWN,
    /** The owner reset encryption: ask for the new passphrase. */
    val keyWasReset: Boolean = false,
    val passphrase: String = "",
    val repeat: String = "",
    val busy: Boolean = false,
    val error: UiText? = null,
) {
    val canUnlock: Boolean get() = !busy && passphrase.isNotEmpty()
    val canSetUp: Boolean get() = !busy && passphrase.isNotEmpty() && repeat.isNotEmpty()
}

/**
 * The screen between sign-in and the history: unlock by passphrase or by a scanned `Add a device` code, the owner's
 * setup, or what stands in the way (an old server, an old app, no vault yet). The actions are the session manager's;
 * their copy is shown as is. Passphrases live only in this form, and are cleared once used.
 */
class UnlockViewModel(
    vaultState: StateFlow<VaultState>,
    keyWasReset: StateFlow<Boolean>,
    private val unlock: suspend (passphrase: String) -> UiText?,
    private val unlockWithQr: suspend (text: String) -> UiText?,
    private val setUp: suspend (passphrase: String, repeat: String) -> UiText?,
    private val recheck: () -> Unit,
    private val performSignOut: suspend () -> Unit,
) : ViewModel() {

    private val form = MutableStateFlow(UnlockState())

    val state: StateFlow<UnlockState> = combine(vaultState, keyWasReset, form) { vault, reset, f ->
        f.copy(vault = vault, keyWasReset = reset)
    }.stateIn(
        viewModelScope,
        SharingStarted.WhileSubscribed(5_000),
        UnlockState(vault = vaultState.value, keyWasReset = keyWasReset.value),
    )

    fun onPassphraseChange(value: String) = form.update { it.copy(passphrase = value, error = null) }

    fun onRepeatChange(value: String) = form.update { it.copy(repeat = value, error = null) }

    fun unlock() {
        val f = form.value
        if (!f.canUnlock) return
        run { unlock(f.passphrase) }
    }

    fun setUp() {
        val f = form.value
        if (!f.canSetUp) return
        run { setUp(f.passphrase, f.repeat) }
    }

    /** The scanner's result; null when the scan was cancelled. */
    fun onScanned(text: String?) {
        if (text == null || form.value.busy) return
        run { unlockWithQr(text) }
    }

    fun onCameraDenied() = form.update { it.copy(error = UiText.Res(R.string.vault_camera_denied)) }

    fun retry() {
        form.update { it.copy(error = null) }
        recheck()
    }

    fun signOut() {
        viewModelScope.launch {
            try {
                performSignOut()
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                // The session manager clears local state even when the server call fails.
            }
        }
    }

    private fun run(action: suspend () -> UiText?) {
        form.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            val error = try {
                action()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                UiText.Res(R.string.signin_unreachable)
            }
            form.update {
                if (error == null) UnlockState() else it.copy(busy = false, error = error)
            }
        }
    }
}
