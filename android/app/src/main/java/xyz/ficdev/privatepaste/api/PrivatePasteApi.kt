package xyz.ficdev.privatepaste.api

import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.DeserializationStrategy
import kotlinx.serialization.SerializationException
import kotlinx.serialization.SerializationStrategy
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
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipPage
import xyz.ficdev.privatepaste.core.model.ClipUploadMeta
import xyz.ficdev.privatepaste.core.model.HandoverBlob
import xyz.ficdev.privatepaste.core.model.HandoverCreated
import xyz.ficdev.privatepaste.core.model.HandoverRequest
import xyz.ficdev.privatepaste.core.model.LoginResponse
import xyz.ficdev.privatepaste.core.model.Me
import xyz.ficdev.privatepaste.core.model.ProtocolJson
import xyz.ficdev.privatepaste.core.model.ServerInfo
import xyz.ficdev.privatepaste.core.model.SyncPage
import xyz.ficdev.privatepaste.core.model.VaultWrap
import xyz.ficdev.privatepaste.sync.SyncApi
import java.io.File
import java.io.IOException
import java.time.Duration
import java.util.Base64
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
    private val binaryType = "application/octet-stream".toMediaType()

    suspend fun login(username: String, password: String, deviceName: String): LoginResponse {
        val body = buildJsonObject {
            put("username", username)
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

    suspend fun clips(cursor: String?, limit: Int = 50, pinned: Boolean = false): ClipPage {
        val url = url("api/clips") {
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

    /** The sealed thumbnail (ciphertext). */
    override suspend fun thumb(id: String): ByteArray = bytes(url("api/clips") { addPathSegment(id); addPathSegment("thumb") })

    /**
     * Sealed blob [n] of a clip (ciphertext). [expectedBytes] (a file's size) gives a large download more time, as
     * [uploadTimeout] does an upload; 0 keeps the usual timeout.
     */
    suspend fun blob(id: String, n: Int, expectedBytes: Long = 0): ByteArray =
        bytes(url("api/clips") { addPathSegment(id); addPathSegment("blobs"); addPathSegment(n.toString()) }, blobTimeout(expectedBytes))

    /** The usual timeout plus 1 s per started 256 KB of [expectedBytes], the extra capped like [uploadTimeout]'s. */
    internal fun blobTimeout(expectedBytes: Long): Duration =
        requestTimeout.plus(uploadTimeout(expectedBytes).minus(REQUEST_TIMEOUT))

    /** Uploads a sealed clip: [meta], the sealed [thumb] if any, and the sealed [blobs] in order (`blob0`, `blob1`, ...). */
    suspend fun uploadClip(meta: ClipUploadMeta, thumb: ByteArray?, blobs: List<ByteArray>): ClipDto =
        upload(meta, thumb, blobs.map { it.toRequestBody(binaryType) })

    /** Uploads a sealed file clip whose blobs are the sealed [blobs] files, streamed in order. */
    suspend fun uploadFileClip(meta: ClipUploadMeta, blobs: List<File>): ClipDto =
        upload(meta, null, blobs.map { it.asRequestBody(binaryType) })

    private suspend fun upload(meta: ClipUploadMeta, thumb: ByteArray?, blobs: List<RequestBody>): ClipDto {
        val form = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("meta", null, jsonBody(ClipUploadMeta.serializer(), meta))
        if (thumb != null) form.addFormDataPart("thumb", "thumb", thumb.toRequestBody(binaryType))
        blobs.forEachIndexed { i, blob -> form.addFormDataPart("blob$i", "blob$i", blob) }
        val request = request("api/clips").post(form.build()).build()
        val size = meta.payload.length.toLong() + (thumb?.size ?: 0) + blobs.sumOf { it.contentLength() }
        return execute(request, uploadTimeout(size)) { decode(it, ClipDto.serializer()) }
    }

    /** Null from a server that predates protocol 2 (404). Needs no sign-in. */
    suspend fun serverInfo(): ServerInfo? {
        val request = request("api/server-info", authorized = false).get().build()
        return execute(request, requestTimeout, allowNotFound = true) {
            if (it.code == 404) null else decode(it, ServerInfo.serializer())
        }
    }

    suspend fun me(): Me = execute(get(url("api/me")), requestTimeout) { decode(it, Me.serializer()) }

    /** The passphrase-wrapped vault key, or null while encryption is not set up. */
    suspend fun vault(): VaultWrap? = execute(get(url("api/vault")), requestTimeout, allowNotFound = true) {
        if (it.code == 404) null else decode(it, VaultWrap.serializer())
    }

    /** Owner only: sets up encryption, or re-wraps the same key under a new passphrase. */
    suspend fun putVault(wrap: VaultWrap) {
        val request = request("api/vault").put(jsonBody(VaultWrap.serializer(), wrap)).build()
        execute(request, requestTimeout) { }
    }

    /** Owner only: replaces the key and wipes every clip. */
    suspend fun resetVault(wrap: VaultWrap) {
        val request = request("api/vault/reset").post(jsonBody(VaultWrap.serializer(), wrap)).build()
        execute(request, requestTimeout) { }
    }

    suspend fun createHandover(blob: ByteArray): HandoverCreated {
        val body = HandoverRequest(Base64.getEncoder().encodeToString(blob))
        val request = request("api/vault/handovers").post(jsonBody(HandoverRequest.serializer(), body)).build()
        return execute(request, requestTimeout) { decode(it, HandoverCreated.serializer()) }
    }

    /** The handover blob, once: null if it is gone (taken, expired or never made). */
    suspend fun takeHandover(id: String): ByteArray? {
        val url = url("api/vault/handovers") { addPathSegment(id) }
        return execute(get(url), requestTimeout, allowNotFound = true) {
            if (it.code == 404) null else Base64.getDecoder().decode(decode(it, HandoverBlob.serializer()).blob)
        }
    }

    /** Opens `GET /ws` with the bearer header. The caller owns the socket and its reconnect policy. */
    fun webSocket(listener: WebSocketListener): WebSocket {
        val request = request(url("ws")).build()
        val wsClient = client.newBuilder().connectTimeout(WS_CONNECT_TIMEOUT).build()
        return wsClient.newWebSocket(request, listener)
    }

    private suspend fun bytes(url: HttpUrl, timeout: Duration = requestTimeout): ByteArray =
        execute(get(url), timeout) {
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
        val builder = Request.Builder().url(url).header(PROTOCOL_HEADER, PROTOCOL_VERSION)
        if (authorized && !token.isNullOrEmpty()) builder.header("Authorization", "Bearer $token")
        return builder
    }

    private fun get(url: HttpUrl): Request = request(url).get().build()

    private fun jsonBody(body: JsonObject): RequestBody = jsonBody(JsonObject.serializer(), body)

    private fun <T> jsonBody(serializer: SerializationStrategy<T>, body: T): RequestBody =
        ProtocolJson.json.encodeToString(serializer, body).toRequestBody(jsonType)

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
            val message = problem.detail ?: problem.title ?: "Request failed ($code)"
            when {
                code == 426 -> UpdateRequiredException(message)
                code == 409 && problem.title == KeyResetException.TITLE -> KeyResetException(message)
                else -> ApiException.Rejected(code, message, problem.title)
            }
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
        private const val PROTOCOL_HEADER = "X-PrivatePaste-Protocol"
        private const val PROTOCOL_VERSION = "2"
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
