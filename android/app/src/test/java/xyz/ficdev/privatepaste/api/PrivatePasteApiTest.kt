package xyz.ficdev.privatepaste.api

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.Call
import okhttp3.EventListener
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import okhttp3.mockwebserver.SocketPolicy
import okio.Buffer
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import xyz.ficdev.privatepaste.core.model.ClipKind
import java.time.Duration
import java.time.Instant

class PrivatePasteApiTest {
    private lateinit var server: MockWebServer
    private val canceled = CompletableDeferred<Unit>()

    private val client = OkHttpClient.Builder()
        .eventListener(object : EventListener() {
            override fun canceled(call: Call) { canceled.complete(Unit) }
        })
        .build()

    @Before fun setUp() { server = MockWebServer().also { it.start() } }
    @After fun tearDown() { server.shutdown() }

    private fun api(token: String? = "tok", path: String = "/") =
        PrivatePasteApi(server.url(path), token, client)

    private val clipJson = """{"id":"c1","seq":5,"kind":"text","text":"hi","hash":"h","sizeBytes":2,"sourceDeviceId":"d","sourceDeviceName":"Pc","lastCopiedAt":"2026-10-28T10:00:00+00:00"}"""

    private fun json(body: String, code: Int = 200) =
        MockResponse().setResponseCode(code).setHeader("Content-Type", "application/json").setBody(body)

    private fun problem(code: Int, detail: String?, title: String? = null): MockResponse {
        val parts = buildList {
            title?.let { add("\"title\":\"$it\"") }
            detail?.let { add("\"detail\":\"$it\"") }
        }
        return MockResponse().setResponseCode(code).setHeader("Content-Type", "application/problem+json")
            .setBody("{" + parts.joinToString(",") + "}")
    }

    private fun take(): RecordedRequest = server.takeRequest()

    @Test fun loginSendsBodyWithoutBearer() = runBlocking<Unit> {
        server.enqueue(json("""{"deviceId":"d1","token":"t1"}"""))
        val r = api().login("pw", "Pixel")
        assertEquals("t1", r.token)
        val req = take()
        assertEquals("/api/auth/login", req.path)
        assertEquals("POST", req.method)
        assertNull(req.getHeader("Authorization"))
        val body = Json.parseToJsonElement(req.body.readUtf8()).jsonObject
        assertEquals("pw", body["password"]!!.jsonPrimitive.content)
        assertEquals("Pixel", body["deviceName"]!!.jsonPrimitive.content)
        assertEquals("android", body["platform"]!!.jsonPrimitive.content)
    }

    @Test fun bearerOnOtherCalls() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(204))
        server.enqueue(json("""{"items":[],"nextCursor":null}"""))
        server.enqueue(json("""{"clips":[],"deletedIds":[],"latestSeq":0,"hasMore":false}"""))
        server.enqueue(MockResponse().setBody("png"))
        val a = api()
        a.logout(); a.clips(null, null); a.sync(0); a.image("x")
        repeat(4) { assertEquals("Bearer tok", take().getHeader("Authorization")) }
    }

    @Test fun subPathIsKept() = runBlocking<Unit> {
        server.enqueue(json("""{"items":[],"nextCursor":null}"""))
        api(path = "/sub/").clips(null, null)
        assertEquals("/sub/api/clips?limit=50", take().path)
    }

    @Test fun clipsQuery() = runBlocking<Unit> {
        server.enqueue(json("""{"items":[$clipJson],"nextCursor":"n"}"""))
        val page = api().clips("a b&c", "cur", 10)
        assertEquals("n", page.nextCursor)
        assertEquals(ClipKind.TEXT, page.items.single().kind)
        val url = take().requestUrl!!
        assertEquals("a b&c", url.queryParameter("q"))
        assertEquals("cur", url.queryParameter("cursor"))
        assertEquals("10", url.queryParameter("limit"))
    }

    @Test fun syncQuery() = runBlocking<Unit> {
        server.enqueue(json("""{"clips":[],"deletedIds":["x"],"latestSeq":9,"hasMore":true}"""))
        val p = api().sync(7)
        assertEquals(9L, p.latestSeq)
        assertEquals("/api/sync?afterSeq=7&limit=200", take().path)
    }

    @Test fun syncPageWithAWebClipDecodes() = runBlocking<Unit> {
        // Web-copied clips have no source device: a page holding one must decode, not fail as Transient.
        val web = clipJson.replace("\"sourceDeviceId\":\"d\"", "\"sourceDeviceId\":null").replace("\"Pc\"", "\"Web\"")
        server.enqueue(json("""{"clips":[$clipJson,$web],"deletedIds":[],"latestSeq":5,"hasMore":false}"""))
        val p = api().sync(0)
        assertEquals(listOf("d", null), p.clips.map { it.sourceDeviceId })
        assertEquals("Web", p.clips[1].sourceDeviceName)
    }

    @Test fun status401IsUnauthorized() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(401))
        assertThrowsApi<ApiException.Unauthorized> { api().clips(null, null) }
    }

    @Test fun status413CarriesDetail() = runBlocking<Unit> {
        server.enqueue(problem(413, "Text is too large.", "Payload Too Large"))
        val e = assertThrowsApi<ApiException.Rejected> { api().uploadText("x", null, false) }
        assertEquals(413, e.status)
        assertEquals("Text is too large.", e.message)
    }

    @Test fun rejectedFallsBackToTitleThenGeneric() = runBlocking<Unit> {
        server.enqueue(problem(400, null, "Bad Request"))
        assertEquals("Bad Request", assertThrowsApi<ApiException.Rejected> { api().uploadText("x", null, false) }.message)
        server.enqueue(MockResponse().setResponseCode(400).setBody("<html>"))
        val e = assertThrowsApi<ApiException.Rejected> { api().uploadText("x", null, false) }
        assertEquals(400, e.status)
        assertTrue(e.message!!.isNotBlank())
    }

    @Test fun status503And408AreTransient() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(503))
        assertThrowsApi<ApiException.Transient> { api().clips(null, null) }
        server.enqueue(MockResponse().setResponseCode(408))
        assertThrowsApi<ApiException.Transient> { api().clips(null, null) }
    }

    @Test fun status429() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(429))
        assertThrowsApi<ApiException.RateLimited> { api().login("p", "d") }
        server.enqueue(MockResponse().setResponseCode(429))
        assertThrowsApi<ApiException.Transient> { api().clips(null, null) }
    }

    @Test fun loginWrongPasswordIsUnauthorized() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(401))
        assertThrowsApi<ApiException.Unauthorized> { api().login("p", "d") }
    }

    @Test fun undecodable2xxBodiesAreTransient() = runBlocking<Unit> {
        server.enqueue(MockResponse().setHeader("Content-Type", "text/html").setBody("<html>portal</html>"))
        assertThrowsApi<ApiException.Transient> { api().clips(null, null) }
        server.enqueue(json("not json"))
        assertThrowsApi<ApiException.Transient> { api().clips(null, null) }
        server.enqueue(MockResponse().setBody("""{"items":[],"nextCursor":null}""")) // no content type
        assertThrowsApi<ApiException.Transient> { api().clips(null, null) }
        val unknownKind = clipJson.replace("\"kind\":\"text\"", "\"kind\":\"video\"")
        server.enqueue(json("""{"items":[$unknownKind]}"""))
        assertThrowsApi<ApiException.Transient> { api().clips(null, null) }
    }

    @Test fun networkFailureIsTransient() = runBlocking<Unit> {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.DISCONNECT_AFTER_REQUEST))
        assertThrowsApi<ApiException.Transient> { api().clips(null, null) }
    }

    @Test fun uploadTextBody() = runBlocking<Unit> {
        server.enqueue(json(clipJson))
        api().uploadText("hello", null, false)
        val live = Json.parseToJsonElement(take().body.readUtf8()).jsonObject
        assertEquals("hello", live["text"]!!.jsonPrimitive.content)
        assertEquals("false", live["backfill"]!!.jsonPrimitive.content)
        assertFalse(live.containsKey("copiedAt"))

        server.enqueue(json(clipJson))
        api().uploadText("hello", Instant.parse("2026-10-28T10:00:00Z"), true)
        val back = Json.parseToJsonElement(take().body.readUtf8()).jsonObject
        assertEquals("2026-10-28T10:00:00Z", back["copiedAt"]!!.jsonPrimitive.content)
        assertEquals("true", back["backfill"]!!.jsonPrimitive.content)
    }

    @Test fun liveImageUploadHasOnlyFilePart() = runBlocking<Unit> {
        server.enqueue(json(clipJson))
        api().uploadImage(byteArrayOf(1, 2, 3), "a.png", "image/png", null, false)
        val req = take()
        assertEquals("/api/clips/image", req.path)
        val body = req.body.readUtf8()
        assertTrue(body.contains("name=\"file\"; filename=\"a.png\""))
        assertTrue(body.contains("Content-Type: image/png"))
        assertFalse(body.contains("name=\"copiedAt\""))
        assertFalse(body.contains("name=\"backfill\""))
    }

    @Test fun backfillImageUploadHasAllParts() = runBlocking<Unit> {
        server.enqueue(json(clipJson))
        api().uploadImage(byteArrayOf(1, 2, 3), "a.jpg", "image/jpeg", Instant.parse("2026-10-28T10:00:00Z"), true)
        val body = take().body.readUtf8()
        assertTrue(body.contains("name=\"file\"; filename=\"a.jpg\""))
        assertTrue(body.contains("name=\"copiedAt\"\r\n\r\n2026-10-28T10:00:00Z"))
        assertTrue(body.contains("name=\"backfill\"\r\n\r\ntrue"))
    }

    @Test fun restoreMaps404ToNull() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(404))
        assertNull(api().restore("x"))
        server.enqueue(json(clipJson))
        assertEquals("c1", api().restore("c1")!!.id)
        take()
        val req = take()
        assertEquals("POST", req.method)
        assertEquals("/api/clips/c1/restore", req.path)
    }

    @Test fun deleteMaps404ToFalse() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(404))
        assertFalse(api().delete("x"))
        server.enqueue(MockResponse().setResponseCode(204))
        assertTrue(api().delete("c1"))
        take()
        val req = take()
        assertEquals("DELETE", req.method)
        assertEquals("/api/clips/c1", req.path)
    }

    @Test fun pinAndUnpinCallTheirEndpoints() = runBlocking<Unit> {
        server.enqueue(json(clipJson.replace("\"hash\"", "\"pinnedAt\":\"2026-10-29T09:00:00+00:00\",\"hash\"")))
        val pinned = api().pin("c1", true)
        assertEquals("2026-10-29T09:00:00+00:00", pinned.pinnedAt)
        server.enqueue(json(clipJson))
        assertNull(api().pin("c1", false).pinnedAt)
        val pin = take()
        assertEquals("POST", pin.method)
        assertEquals("/api/clips/c1/pin", pin.path)
        val unpin = take()
        assertEquals("DELETE", unpin.method)
        assertEquals("/api/clips/c1/pin", unpin.path)
    }

    @Test fun pinOfAMissingClipIsRejected404() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(404))
        try {
            api().pin("x", true)
            org.junit.Assert.fail("expected Rejected")
        } catch (e: ApiException.Rejected) {
            assertEquals(404, e.status)
        }
    }

    @Test fun pinnedListAsksForPinnedTrue() = runBlocking<Unit> {
        server.enqueue(json("""{"items":[],"nextCursor":null}"""))
        api().clips(q = "x", cursor = null, limit = 50, pinned = true)
        assertEquals("/api/clips?q=x&limit=50&pinned=true", take().path)
    }

    @Test fun deleteOther4xxStillThrows() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(401))
        assertThrowsApi<ApiException.Unauthorized> { api().delete("x") }
    }

    @Test fun imageAndThumbnailBytes() = runBlocking<Unit> {
        server.enqueue(MockResponse().setBody(Buffer().write(byteArrayOf(1, 2, 3))))
        assertArrayEquals(byteArrayOf(1, 2, 3), api().image("c1"))
        server.enqueue(MockResponse().setBody(Buffer().write(byteArrayOf(9))))
        assertArrayEquals(byteArrayOf(9), api().thumbnail("c1"))
        assertEquals("/api/clips/c1/image", take().path)
        assertEquals("/api/clips/c1/thumbnail", take().path)
    }

    @Test fun webSocketSendsBearer() {
        server.enqueue(MockResponse().withWebSocketUpgrade(object : WebSocketListener() {}))
        val opened = CompletableDeferred<Unit>()
        val ws = api().webSocket(object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) { opened.complete(Unit) }
        })
        runBlocking { withTimeout(5000) { opened.await() } }
        val req = take()
        assertEquals("/ws", req.path)
        assertEquals("Bearer tok", req.getHeader("Authorization"))
        ws.cancel()
    }

    @Test fun cancellingTheCoroutineCancelsTheCall() = runBlocking<Unit> {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        val job = launch(Dispatchers.IO) { api().clips(null, null) }
        take() // the request arrived; the server never answers
        job.cancel()
        withTimeout(5000) { canceled.await() }
        job.join()
    }

    @Test fun uploadTimeouts() {
        assertEquals(Duration.ofSeconds(31), PrivatePasteApi.uploadTimeout(1))
        assertEquals(Duration.ofSeconds(30), PrivatePasteApi.uploadTimeout(0))
        assertEquals(Duration.ofSeconds(31), PrivatePasteApi.uploadTimeout(256L * 1024))
        assertEquals(Duration.ofSeconds(32), PrivatePasteApi.uploadTimeout(256L * 1024 + 1))
        assertEquals(Duration.ofMinutes(5), PrivatePasteApi.uploadTimeout(100L * 1024 * 1024))
    }

    @Test fun idWithSlashStaysOneEncodedSegment() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(204))
        api().delete("a/b")
        assertEquals("/api/clips/a%2Fb", take().path)
    }

    @Test fun callTimeoutFiresAsTransient() = runBlocking<Unit> {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        val a = api().also { it.requestTimeout = Duration.ofMillis(200) }
        assertThrowsApi<ApiException.Transient> { a.clips(null, null) }
    }

    private suspend inline fun <reified T : ApiException> assertThrowsApi(block: () -> Unit): T {
        try {
            block()
        } catch (e: ApiException) {
            assertTrue("expected ${T::class.simpleName} but was $e", e is T)
            return e as T
        }
        throw AssertionError("expected ${T::class.simpleName}, nothing thrown")
    }
}
