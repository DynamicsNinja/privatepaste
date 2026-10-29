package xyz.ficdev.privatepaste.api

import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.DeserializationStrategy
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipPage
import xyz.ficdev.privatepaste.core.model.LoginResponse
import xyz.ficdev.privatepaste.core.model.ProtocolJson
import xyz.ficdev.privatepaste.core.model.SyncPage
import xyz.ficdev.privatepaste.sync.SyncApi
import java.io.IOException
import java.time.Duration
import java.time.Instant
import kotlin.coroutines.resumeWithException

/**
 * Typed client for the PrivatePaste server API (README "API"). [baseUrl] must end with `/` (see `ServerUrl`).
 * Every failure is an [ApiException]; coroutine cancellation cancels the underlying OkHttp call.
 * Each call gets its own timeout through `Call.timeout()`, so [client] should leave `callTimeout` unset.
 */
class PrivatePasteApi(
    private val baseUrl: HttpUrl,
    private val token: String?,
    private val client: OkHttpClient,
) : SyncApi {
    /** Timeout of calls that are not uploads. Tests shorten it. */
    internal var requestTimeout: Duration = REQUEST_TIMEOUT

    private val jsonType = "application/json; charset=utf-8".toMediaType()

    suspend fun login(password: String, deviceName: String): LoginResponse {
        val body = buildJsonObject {
            put("password", password)
            put("deviceName", deviceName)
            put("platform", "android")
        }
        val request = request("api/auth/login", authorized = false).post(jsonBody(body)).build()
        return execute(request, requestTimeout, isLogin = true) { decode(it, LoginResponse.serializer()) }
    }

    suspend fun logout() {
        val request = request("api/auth/logout").post(ByteArray(0).toRequestBody()).build()
        execute(request, requestTimeout) { }
    }

    suspend fun uploadText(text: String, copiedAt: Instant?, backfill: Boolean): ClipDto {
        val body = buildJsonObject {
            put("text", text)
            if (backfill && copiedAt != null) put("copiedAt", copiedAt.toString())
            put("backfill", backfill)
        }
        val request = request("api/clips/text").post(jsonBody(body)).build()
        val timeout = uploadTimeout(text.toByteArray(Charsets.UTF_8).size.toLong())
        return execute(request, timeout) { decode(it, ClipDto.serializer()) }
    }

    suspend fun uploadImage(
        bytes: ByteArray,
        fileName: String,
        mime: String,
        copiedAt: Instant?,
        backfill: Boolean,
    ): ClipDto {
        val form = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("file", fileName, bytes.toRequestBody(mime.toMediaType()))
        if (backfill) {
            if (copiedAt != null) form.addFormDataPart("copiedAt", copiedAt.toString())
            form.addFormDataPart("backfill", "true")
        }
        val request = request("api/clips/image").post(form.build()).build()
        return execute(request, uploadTimeout(bytes.size.toLong())) { decode(it, ClipDto.serializer()) }
    }

    suspend fun clips(q: String?, cursor: String?, limit: Int = 50, pinned: Boolean = false): ClipPage {
        val url = url("api/clips") {
            if (!q.isNullOrEmpty()) addQueryParameter("q", q)
            if (!cursor.isNullOrEmpty()) addQueryParameter("cursor", cursor)
            addQueryParameter("limit", limit.toString())
            if (pinned) addQueryParameter("pinned", "true")
        }
        return execute(get(url), requestTimeout) { decode(it, ClipPage.serializer()) }
    }

    /** Bumps the clip as freshly copied. Null if it no longer exists. */
    suspend fun restore(id: String): ClipDto? {
        val request = request(url("api/clips") { addPathSegment(id); addPathSegment("restore") }).post(ByteArray(0).toRequestBody()).build()
        return execute(request, requestTimeout, allowNotFound = true) {
            if (it.code == 404) null else decode(it, ClipDto.serializer())
        }
    }

    /** Pins or unpins a clip (history only: it never reaches a clipboard). A missing clip is a 404 [ApiException.Rejected]. */
    suspend fun pin(id: String, pinned: Boolean): ClipDto {
        val url = url("api/clips") { addPathSegment(id); addPathSegment("pin") }
        val request = if (pinned) request(url).post(ByteArray(0).toRequestBody()).build() else request(url).delete().build()
        return execute(request, requestTimeout) { decode(it, ClipDto.serializer()) }
    }

    /** False if the clip no longer exists. */
    suspend fun delete(id: String): Boolean {
        val request = request(url("api/clips") { addPathSegment(id) }).delete().build()
        return execute(request, requestTimeout, allowNotFound = true) { it.code != 404 }
    }

    /** `limit` defaults to 200 (declared on [SyncApi]). */
    override suspend fun sync(afterSeq: Long, limit: Int): SyncPage {
        val url = url("api/sync") {
            addQueryParameter("afterSeq", afterSeq.toString())
            addQueryParameter("limit", limit.toString())
        }
        return execute(get(url), requestTimeout) { decode(it, SyncPage.serializer()) }
    }

    suspend fun image(id: String): ByteArray = bytes(url("api/clips") { addPathSegment(id); addPathSegment("image") })

    override suspend fun thumbnail(id: String): ByteArray = bytes(url("api/clips") { addPathSegment(id); addPathSegment("thumbnail") })

    /** Opens `GET /ws` with the bearer header. The caller owns the socket and its reconnect policy. */
    fun webSocket(listener: WebSocketListener): WebSocket {
        val request = request(url("ws")).build()
        val wsClient = client.newBuilder().connectTimeout(WS_CONNECT_TIMEOUT).build()
        return wsClient.newWebSocket(request, listener)
    }

    private suspend fun bytes(url: HttpUrl): ByteArray =
        execute(get(url), requestTimeout) {
            try {
                it.body.bytes()
            } catch (e: IOException) {
                throw ApiException.Transient("Connection lost", e)
            }
        }

    private fun url(path: String, configure: HttpUrl.Builder.() -> Unit = {}): HttpUrl =
        baseUrl.newBuilder().addPathSegments(path).apply(configure).build()

    private fun request(path: String, authorized: Boolean = true): Request.Builder =
        request(url(path), authorized)

    private fun request(url: HttpUrl, authorized: Boolean = true): Request.Builder {
        val builder = Request.Builder().url(url)
        if (authorized && !token.isNullOrEmpty()) builder.header("Authorization", "Bearer $token")
        return builder
    }

    private fun get(url: HttpUrl): Request = request(url).get().build()

    private fun jsonBody(body: JsonObject): RequestBody =
        ProtocolJson.json.encodeToString(JsonObject.serializer(), body).toRequestBody(jsonType)

    private fun <T> decode(response: Response, serializer: DeserializationStrategy<T>): T {
        val type = response.body.contentType()
        if (type == null || !(type.subtype == "json" || type.subtype.endsWith("+json"))) {
            throw ApiException.Transient("Unexpected response from the server")
        }
        val text = try {
            response.body.string()
        } catch (e: IOException) {
            throw ApiException.Transient("Connection lost", e)
        }
        return try {
            ProtocolJson.json.decodeFromString(serializer, text)
        } catch (e: SerializationException) {
            throw ApiException.Transient("Unexpected response from the server", e)
        } catch (e: IllegalArgumentException) {
            throw ApiException.Transient("Unexpected response from the server", e)
        }
    }

    /**
     * Runs [request] under its own [timeout]. [onSuccess] reads the body on OkHttp's thread for 2xx answers (and for
     * 404 when [allowNotFound]); every other status is mapped to an [ApiException].
     */
    private suspend fun <T> execute(
        request: Request,
        timeout: Duration,
        isLogin: Boolean = false,
        allowNotFound: Boolean = false,
        onSuccess: (Response) -> T,
    ): T = suspendCancellableCoroutine { cont ->
        val call = client.newCall(request)
        call.timeout().timeout(timeout.toMillis(), java.util.concurrent.TimeUnit.MILLISECONDS)
        cont.invokeOnCancellation { call.cancel() }
        call.enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                if (cont.isActive) cont.resumeWithException(ApiException.Transient(e.message ?: "Network error", e))
            }

            override fun onResponse(call: Call, response: Response) {
                val result: Result<T> = response.use {
                    try {
                        if (it.isSuccessful || (allowNotFound && it.code == 404)) {
                            Result.success(onSuccess(it))
                        } else {
                            Result.failure(error(it, isLogin))
                        }
                    } catch (e: ApiException) {
                        Result.failure(e)
                    } catch (e: IOException) {
                        Result.failure(ApiException.Transient(e.message ?: "Network error", e))
                    } catch (e: Exception) {
                        Result.failure(ApiException.Transient("Unexpected response from the server", e))
                    }
                }
                if (cont.isActive) cont.resumeWith(result)
            }
        })
    }

    private fun error(response: Response, isLogin: Boolean): ApiException = when (val code = response.code) {
        401 -> ApiException.Unauthorized()
        429 -> if (isLogin) ApiException.RateLimited() else ApiException.Transient("Too many requests")
        408 -> ApiException.Transient("Request timed out")
        in 400..499 -> {
            val problem = problem(response)
            ApiException.Rejected(code, problem.detail ?: problem.title ?: "Request failed ($code)", problem.title)
        }
        else -> ApiException.Transient("Server error ($code)")
    }

    private class Problem(val title: String?, val detail: String?)

    /** RFC 7807 `title` and `detail`; the message shown is the detail, falling back to the title. */
    private fun problem(response: Response): Problem = try {
        val obj = Json.parseToJsonElement(response.body.string()).jsonObject
        fun text(key: String) = (obj[key] as? JsonPrimitive)?.takeIf { it.isString }?.content?.takeIf { it.isNotBlank() }
        Problem(text("title"), text("detail"))
    } catch (_: Exception) {
        Problem(null, null)
    }

    companion object {
        private val REQUEST_TIMEOUT: Duration = Duration.ofSeconds(30)
        private val WS_CONNECT_TIMEOUT: Duration = Duration.ofSeconds(15)
        private const val BYTES_PER_EXTRA_SECOND = 256L * 1024
        private const val MAX_EXTRA_SECONDS = 270L // 5 min cap minus the 30 s base

        /** 30 s plus 1 s per started 256 KB, capped at 5 min. */
        fun uploadTimeout(bytes: Long): Duration {
            val extra = (maxOf(0L, bytes) + BYTES_PER_EXTRA_SECOND - 1) / BYTES_PER_EXTRA_SECOND
            return REQUEST_TIMEOUT.plusSeconds(minOf(extra, MAX_EXTRA_SECONDS))
        }
    }
}
