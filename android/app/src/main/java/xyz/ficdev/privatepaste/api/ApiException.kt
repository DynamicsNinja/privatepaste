package xyz.ficdev.privatepaste.api

/** Failure of a server call. Callers decide on retry from the subtype. */
sealed class ApiException(message: String, cause: Throwable? = null) : Exception(message, cause) {
    /** 401: the token (or password) is not accepted. */
    class Unauthorized : ApiException("Unauthorized")

    /** Any other 4xx (400, 404, 413, ...): the server refused the request; retrying will not help. */
    class Rejected(val status: Int, message: String, val title: String? = null) : ApiException(message)

    /** 429 on login. */
    class RateLimited : ApiException("Too many attempts")

    /** IO errors, timeouts, 5xx, 408, 429 outside login and 2xx bodies that cannot be decoded: retry later. */
    class Transient(message: String, cause: Throwable? = null) : ApiException(message, cause)
}
