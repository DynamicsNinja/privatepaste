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
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ClipUploadMeta
import xyz.ficdev.privatepaste.core.model.ProtocolJson
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
        val r = api().login("Ana", "pw", "Pixel")
        assertEquals("t1", r.token)
        val req = take()
        assertEquals("/api/auth/login", req.path)
        assertEquals("POST", req.method)
        assertNull(req.getHeader("Authorization"))
        val body = Json.parseToJsonElement(req.body.readUtf8()).jsonObject
        assertEquals("Ana", body["username"]!!.jsonPrimitive.content)
        assertEquals("pw", body["password"]!!.jsonPrimitive.content)
        assertEquals("Pixel", body["deviceName"]!!.jsonPrimitive.content)
        assertEquals("android", body["platform"]!!.jsonPrimitive.content)
    }

    @Test fun login_sendsUsername() = runBlocking<Unit> {
        server.enqueue(json("""{"deviceId":"d1","token":"t1"}"""))
        api().login("Ana", "pw", "Pixel")
        val body = Json.parseToJsonElement(take().body.readUtf8()).jsonObject
        assertEquals("Ana", body["username"]!!.jsonPrimitive.content)
    }

    @Test fun login400CarriesTheServersDetail() = runBlocking<Unit> {
        server.enqueue(problem(400, "Update PrivatePaste to sign in."))
        val e = assertThrowsApi<ApiException.Rejected> { api().login("", "pw", "Pixel") }
        assertEquals("Update PrivatePaste to sign in.", e.message)
    }

    @Test fun bearerOnOtherCalls() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(204))
        server.enqueue(json("""{"items":[],"nextCursor":null}"""))
        server.enqueue(json("""{"clips":[],"deletedIds":[],"latestSeq":0,"hasMore":false}"""))
        server.enqueue(MockResponse().setBody("sealed"))
        val a = api()
        a.logout(); a.clips(null); a.sync(0); a.blob("x", 0)
        repeat(4) { assertEquals("Bearer tok", take().getHeader("Authorization")) }
    }

    @Test fun subPathIsKept() = runBlocking<Unit> {
        server.enqueue(json("""{"items":[],"nextCursor":null}"""))
        api(path = "/sub/").clips(null)
        assertEquals("/sub/api/clips?limit=50", take().path)
    }

    @Test fun clipsQuery() = runBlocking<Unit> {
        server.enqueue(json("""{"items":[$clipJson],"nextCursor":"n"}"""))
        val page = api().clips("cur", 10)
        assertEquals("n", page.nextCursor)
        assertEquals(ClipKind.TEXT, page.items.single().kind)
        val url = take().requestUrl!!
        assertNull(url.queryParameter("q"))
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
        assertThrowsApi<ApiException.Unauthorized> { api().clips(null) }
    }

    @Test fun status413CarriesDetail() = runBlocking<Unit> {
        server.enqueue(problem(413, "Text is too large.", "Payload Too Large"))
        val e = assertThrowsApi<ApiException.Rejected> { api().uploadClip(meta, null, emptyList()) }
        assertEquals(413, e.status)
        assertEquals("Text is too large.", e.message)
    }

    @Test fun rejectedFallsBackToTitleThenGeneric() = runBlocking<Unit> {
        server.enqueue(problem(400, null, "Bad Request"))
        assertEquals("Bad Request", assertThrowsApi<ApiException.Rejected> { api().uploadClip(meta, null, emptyList()) }.message)
        server.enqueue(MockResponse().setResponseCode(400).setBody("<html>"))
        val e = assertThrowsApi<ApiException.Rejected> { api().uploadClip(meta, null, emptyList()) }
        assertEquals(400, e.status)
        assertTrue(e.message!!.isNotBlank())
    }

    @Test fun status503And408AreTransient() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(503))
        assertThrowsApi<ApiException.Transient> { api().clips(null) }
        server.enqueue(MockResponse().setResponseCode(408))
        assertThrowsApi<ApiException.Transient> { api().clips(null) }
    }

    @Test fun status429() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(429))
        assertThrowsApi<ApiException.RateLimited> { api().login("n", "p", "d") }
        server.enqueue(MockResponse().setResponseCode(429))
        assertThrowsApi<ApiException.Transient> { api().clips(null) }
    }

    @Test fun loginWrongPasswordIsUnauthorized() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(401))
        assertThrowsApi<ApiException.Unauthorized> { api().login("n", "p", "d") }
    }

    @Test fun undecodable2xxBodiesAreTransient() = runBlocking<Unit> {
        server.enqueue(MockResponse().setHeader("Content-Type", "text/html").setBody("<html>portal</html>"))
        assertThrowsApi<ApiException.Transient> { api().clips(null) }
        server.enqueue(json("not json"))
        assertThrowsApi<ApiException.Transient> { api().clips(null) }
        server.enqueue(MockResponse().setBody("""{"items":[],"nextCursor":null}""")) // no content type
        assertThrowsApi<ApiException.Transient> { api().clips(null) }
        val noHash = clipJson.replace("\"hash\":\"h\",", "")
        server.enqueue(json("""{"items":[$noHash]}"""))
        assertThrowsApi<ApiException.Transient> { api().clips(null) }
        // An unknown kind is lenient: it decodes as UNKNOWN instead of failing the page.
        server.enqueue(json("""{"items":[${clipJson.replace("\"kind\":\"text\"", "\"kind\":\"video\"")}]}"""))
        assertEquals(ClipKind.UNKNOWN, api().clips(null).items.single().kind)
    }

    @Test fun networkFailureIsTransient() = runBlocking<Unit> {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.DISCONNECT_AFTER_REQUEST))
        assertThrowsApi<ApiException.Transient> { api().clips(null) }
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
        api().clips(cursor = null, limit = 50, pinned = true)
        assertEquals("/api/clips?limit=50&pinned=true", take().path)
    }

    @Test fun deleteOther4xxStillThrows() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(401))
        assertThrowsApi<ApiException.Unauthorized> { api().delete("x") }
    }

    @Test fun imageAndThumbnailBytes() = runBlocking<Unit> {
        server.enqueue(MockResponse().setBody(Buffer().write(byteArrayOf(9))))
        assertArrayEquals(byteArrayOf(9), api().thumb("c1"))
        server.enqueue(MockResponse().setBody(Buffer().write(byteArrayOf(7, 7))))
        assertArrayEquals(byteArrayOf(7, 7), api().blob("c1", 0))
        assertEquals("/api/clips/c1/thumb", take().path)
        assertEquals("/api/clips/c1/blobs/0", take().path)
    }

    private val meta = ClipUploadMeta(
        kind = "image", keyId = "0011223344556677", hash = "a".repeat(64), payload = "UEFZ", sizeBytes = 3,
        copiedAt = Instant.parse("2026-10-28T10:00:00Z"), backfill = true,
    )

    @Test fun everyRequestSendsProtocolHeader() = runBlocking<Unit> {
        server.enqueue(json("""{"deviceId":"d1","token":"t1"}"""))
        server.enqueue(json("""{"protocol":2,"kinds":["text"],"limits":{"textKb":1,"imageMb":1}}"""))
        server.enqueue(json("""{"name":"Ana","isOwner":true}"""))
        server.enqueue(json(clipJson))
        server.enqueue(MockResponse().setBody("x"))
        val a = api()
        a.login("Ana", "pw", "Pixel"); a.serverInfo(); a.me(); a.uploadClip(meta, null, emptyList()); a.blob("c1", 0)
        repeat(5) { assertEquals("2", take().getHeader("X-PrivatePaste-Protocol")) }
        // The WebSocket upgrade carries it too.
        val ws = a.webSocket(object : WebSocketListener() {})
        assertEquals("2", take().getHeader("X-PrivatePaste-Protocol"))
        ws.cancel()
    }

    @Test fun unknownKindParsesAsUnknown() {
        val clip = ProtocolJson.json.decodeFromString(ClipDto.serializer(), clipJson.replace("\"kind\":\"text\"", "\"kind\":\"rich\""))
        assertEquals(ClipKind.UNKNOWN, clip.kind)
    }

    @Test fun uploadClipSendsMetaThumbAndBlobs() = runBlocking<Unit> {
        server.enqueue(json(clipJson))
        api().uploadClip(meta, byteArrayOf(1, 2), listOf(byteArrayOf(3), byteArrayOf(4)))
        val req = take()
        assertEquals("/api/clips", req.path)
        assertEquals("POST", req.method)
        assertTrue(req.getHeader("Content-Type")!!.startsWith("multipart/form-data"))
        val body = req.body.readUtf8()
        val metaJson = Regex("name=\"meta\"[^{]*(\\{[^\\r]*\\})").find(body)!!.groupValues[1]
        val m = Json.parseToJsonElement(metaJson).jsonObject
        assertEquals("image", m["kind"]!!.jsonPrimitive.content)
        assertEquals("0011223344556677", m["keyId"]!!.jsonPrimitive.content)
        assertEquals("UEFZ", m["payload"]!!.jsonPrimitive.content)
        assertEquals("3", m["sizeBytes"]!!.jsonPrimitive.content)
        assertEquals("2026-10-28T10:00:00Z", m["copiedAt"]!!.jsonPrimitive.content)
        assertEquals("true", m["backfill"]!!.jsonPrimitive.content)
        assertTrue(body.contains("name=\"thumb\""))
        assertTrue(body.contains("name=\"blob0\""))
        assertTrue(body.contains("name=\"blob1\""))
        assertFalse(body.contains("name=\"blob2\""))
    }

    @Test fun uploadClipWithoutThumbHasNoThumbPart() = runBlocking<Unit> {
        server.enqueue(json(clipJson))
        api().uploadClip(meta.copy(kind = "text", copiedAt = null, backfill = false), null, emptyList())
        val body = take().body.readUtf8()
        assertFalse(body.contains("name=\"thumb\""))
        assertFalse(body.contains("name=\"blob0\""))
        assertFalse(body.contains("copiedAt"))
    }

    @Test fun `409ResetMapsToKeyResetException`() = runBlocking<Unit> {
        server.enqueue(problem(409, null, "Encryption was reset"))
        val e = assertThrowsApi<KeyResetException> { api().uploadClip(meta, null, emptyList()) }
        assertEquals(409, e.status)
    }

    @Test fun other409StaysRejected() = runBlocking<Unit> {
        server.enqueue(problem(409, "Set up encryption first.", "Conflict"))
        val e = assertThrowsApi<ApiException.Rejected> { api().uploadClip(meta, null, emptyList()) }
        assertFalse(e is KeyResetException)
        assertEquals("Set up encryption first.", e.message)
    }

    @Test fun `426MapsToUpdateRequired`() = runBlocking<Unit> {
        server.enqueue(problem(426, "Update PrivatePaste to keep syncing."))
        val e = assertThrowsApi<UpdateRequiredException> { api().sync(0) }
        assertEquals("Update PrivatePaste to keep syncing.", e.message)
    }

    @Test fun serverInfo404IsNull() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(404))
        assertNull(api(token = null).serverInfo())
        val req = take()
        assertEquals("/api/server-info", req.path)
        assertNull(req.getHeader("Authorization"))
    }

    @Test fun serverInfoParses() = runBlocking<Unit> {
        server.enqueue(json("""{"protocol":2,"kinds":["text","link","image"],"limits":{"textKb":512,"imageMb":20}}"""))
        val info = api().serverInfo()!!
        assertEquals(2, info.protocol)
        assertEquals(listOf("text", "link", "image"), info.kinds)
        assertEquals(20, info.limits.imageMb)
    }

    @Test fun vaultCallsUseTheVaultRoutes() = runBlocking<Unit> {
        val wire = """{"keyId":"k","kdf":{"alg":"argon2id","memoryKiB":65536,"iterations":3,"parallelism":1,"salt":"c2FsdA=="},"nonce":"bg==","wrappedKey":"d3I="}"""
        server.enqueue(MockResponse().setResponseCode(404))
        server.enqueue(json(wire))
        server.enqueue(json(wire, 201))
        server.enqueue(json(wire))
        val a = api()
        assertNull(a.vault())
        val wrap = a.vault()!!
        assertEquals(65536, wrap.kdf.memoryKiB)
        a.putVault(wrap)
        a.resetVault(wrap)
        assertEquals("GET /api/vault", take().let { "${it.method} ${it.path}" })
        take()
        val put = take()
        assertEquals("PUT /api/vault", "${put.method} ${put.path}")
        assertEquals("d3I=", Json.parseToJsonElement(put.body.readUtf8()).jsonObject["wrappedKey"]!!.jsonPrimitive.content)
        val reset = take()
        assertEquals("POST /api/vault/reset", "${reset.method} ${reset.path}")
    }

    @Test fun handoverRoundTrip() = runBlocking<Unit> {
        server.enqueue(json("""{"id":"00000000-0000-4000-8000-000000000001","expiresAt":"2026-10-28T10:10:00+00:00"}"""))
        server.enqueue(json("""{"blob":"AQID"}"""))
        server.enqueue(MockResponse().setResponseCode(404))
        val a = api()
        val created = a.createHandover(byteArrayOf(1, 2, 3))
        assertEquals("00000000-0000-4000-8000-000000000001", created.id)
        assertEquals(Instant.parse("2026-10-28T10:10:00Z"), created.expiresAt)
        assertArrayEquals(byteArrayOf(1, 2, 3), a.takeHandover(created.id))
        assertNull(a.takeHandover("gone"))
        val post = take()
        assertEquals("POST /api/vault/handovers", "${post.method} ${post.path}")
        assertEquals("AQID", Json.parseToJsonElement(post.body.readUtf8()).jsonObject["blob"]!!.jsonPrimitive.content)
        assertEquals("/api/vault/handovers/00000000-0000-4000-8000-000000000001", take().path)
    }

    @Test fun meParses() = runBlocking<Unit> {
        server.enqueue(json("""{"name":"Ana","isOwner":true}"""))
        val me = api().me()
        assertEquals("Ana", me.name)
        assertTrue(me.isOwner)
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
        val job = launch(Dispatchers.IO) { api().clips(null) }
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

    @Test fun blobTimeoutScalesWithTheExpectedSize() = runBlocking<Unit> {
        val a = api().also { it.requestTimeout = Duration.ofMillis(200) }
        assertEquals(Duration.ofMillis(200), a.blobTimeout(0))
        assertEquals(Duration.ofMillis(1200), a.blobTimeout(1))
        assertEquals(Duration.ofMillis(200).plusSeconds(270), a.blobTimeout(1L shl 40))

        // A body that takes longer than the base timeout: a small blob gives up, a large one waits for it.
        server.enqueue(MockResponse().setBody(Buffer().write(byteArrayOf(1))).setBodyDelay(600, java.util.concurrent.TimeUnit.MILLISECONDS))
        assertThrowsApi<ApiException.Transient> { a.blob("c1", 0) }
        server.enqueue(MockResponse().setBody(Buffer().write(byteArrayOf(2))).setBodyDelay(600, java.util.concurrent.TimeUnit.MILLISECONDS))
        assertArrayEquals(byteArrayOf(2), a.blob("c1", 1, expectedBytes = 10L * 1024 * 1024))
    }

    @Test fun uploadFileClipStreamsBlobFiles() = runBlocking<Unit> {
        server.enqueue(json(clipJson))
        val dir = java.nio.file.Files.createTempDirectory("pp-api").toFile()
        try {
            val blobs = listOf(java.io.File(dir, "0").apply { writeBytes(byteArrayOf(5, 6)) }, java.io.File(dir, "1").apply { writeBytes(byteArrayOf(7)) })
            api().uploadFileClip(meta.copy(kind = "file"), blobs)
            val body = take().body.readUtf8()
            assertTrue(body.contains("name=\"blob0\""))
            assertTrue(body.contains("name=\"blob1\""))
            assertFalse(body.contains("name=\"thumb\""))
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test fun idWithSlashStaysOneEncodedSegment() = runBlocking<Unit> {
        server.enqueue(MockResponse().setResponseCode(204))
        api().delete("a/b")
        assertEquals("/api/clips/a%2Fb", take().path)
    }

    @Test fun callTimeoutFiresAsTransient() = runBlocking<Unit> {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        val a = api().also { it.requestTimeout = Duration.ofMillis(200) }
        assertThrowsApi<ApiException.Transient> { a.clips(null) }
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
