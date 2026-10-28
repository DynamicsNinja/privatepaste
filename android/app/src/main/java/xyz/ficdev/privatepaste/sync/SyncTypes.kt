package xyz.ficdev.privatepaste.sync

/** Connection state of the sync engine, shown in the notification and the history screen. */
enum class SyncState { SIGNED_OUT, CONNECTING, CONNECTED, OFFLINE }

/** Outcome of [SessionManager.signIn]; the sign-in screen maps each to its message. */
sealed interface SignInResult {
    data object Ok : SignInResult
    data object WrongPassword : SignInResult
    data object RateLimited : SignInResult
    data class InvalidUrl(val message: String) : SignInResult
    data class Unreachable(val message: String) : SignInResult
    data class Failed(val message: String) : SignInResult
}
