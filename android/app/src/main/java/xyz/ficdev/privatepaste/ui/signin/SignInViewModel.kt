package xyz.ficdev.privatepaste.ui.signin

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.sync.SignInResult
import xyz.ficdev.privatepaste.ui.UiText

/**
 * The sign-in form. [signIn] is the session manager's; [onSignedIn] starts the sync service, every time a sign-in
 * succeeds (also when onboarding is already done).
 */
class SignInViewModel(
    initialUrl: String?,
    defaultDeviceName: String,
    initialUsername: String? = null,
    private val signIn: suspend (url: String, username: String, password: String, deviceName: String) -> SignInResult,
    private val onSignedIn: () -> Unit,
) : ViewModel() {

    data class State(
        val url: String = "",
        val username: String = "",
        val password: String = "",
        val deviceName: String = "",
        val busy: Boolean = false,
        val error: UiText? = null,
    ) {
        val canSubmit: Boolean
            get() = !busy && url.isNotBlank() && username.isNotBlank() && password.isNotEmpty() && deviceName.trim().length in DEVICE_NAME_LENGTH
    }

    private val _state = MutableStateFlow(
        State(url = initialUrl.orEmpty(), username = initialUsername.orEmpty(), deviceName = defaultDeviceName.trim().take(DEVICE_NAME_LENGTH.last)),
    )
    val state: StateFlow<State> = _state.asStateFlow()

    fun onUrlChange(value: String) = _state.update { it.copy(url = value, error = null) }

    fun onUsernameChange(value: String) = _state.update { it.copy(username = value, error = null) }

    fun onPasswordChange(value: String) = _state.update { it.copy(password = value, error = null) }

    fun onDeviceNameChange(value: String) = _state.update { it.copy(deviceName = value, error = null) }

    fun submit() {
        val form = _state.value
        if (!form.canSubmit) return
        _state.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            val result = try {
                signIn(form.url.trim(), form.username.trim(), form.password, form.deviceName.trim())
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                SignInResult.Failed(e.message ?: e.javaClass.simpleName)
            }
            val error = errorFor(result)
            _state.update {
                if (error == null) it.copy(busy = false, password = "") else it.copy(busy = false, error = error)
            }
            if (result == SignInResult.Ok) onSignedIn()
        }
    }

    private companion object {
        val DEVICE_NAME_LENGTH = 1..50

        fun errorFor(result: SignInResult): UiText? = when (result) {
            SignInResult.Ok -> null
            SignInResult.WrongPassword -> UiText.Res(R.string.signin_wrong_password)
            SignInResult.RateLimited -> UiText.Res(R.string.signin_rate_limited)
            is SignInResult.InvalidUrl -> UiText.Raw(result.message)
            is SignInResult.Unreachable -> UiText.Res(R.string.signin_unreachable)
            is SignInResult.Failed -> UiText.Raw(result.message)
        }
    }
}
