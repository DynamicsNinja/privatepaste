package xyz.ficdev.privatepaste.send

import android.app.Application
import android.graphics.Bitmap
import android.graphics.Color
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartReader
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.core.crypto.ClipCodec
import xyz.ficdev.privatepaste.core.crypto.SealedBox
import xyz.ficdev.privatepaste.core.crypto.VaultKey
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ClipUploadMeta
import xyz.ficdev.privatepaste.core.model.ProtocolJson
import xyz.ficdev.privatepaste.core.model.ServerLimits
import xyz.ficdev.privatepaste.storage.CryptoFile
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.InMemoryKeyProvider
import xyz.ficdev.privatepaste.storage.KeyAliases
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.sync.LocalCaptureClock
import java.io.ByteArrayOutputStream
import java.io.File
import java.time.Clock
import java.time.Instant
import java.time.ZoneOffset
import java.util.Base64
import java.util.concurrent.atomic.AtomicInteger

/** Application that hands activities under test a [ClipSender] (set [sender] before launching). */
class TestSenderApp : Application(), SenderProvider {
    override val clipSender: ClipSender? get() = sender
    override val sendScope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    companion object {
        @Volatile var sender: ClipSender? = null
    }
}

/** One `POST /api/clips` as the server receives it; [text] is the opened payload text (null for an image). */
class Upload(val meta: ClipUploadMeta, val text: String?, val parts: Map<String, ByteArray>)

/** A ClipSender wired to a MockWebServer and real (temp-dir) storage, sealing under [vaultKey]. */
class SenderFixture(val dir: File) {
    val server = MockWebServer().also { it.start() }
    val keys = InMemoryKeyProvider()
    val queue = OutboundQueue(
        CryptoFile(File(dir, "queue.bin"), keys, KeyAliases.QUEUE), File(dir, "queue-images"), keys,
    )
    val cache = HistoryCache(CryptoFile(File(dir, "cache.bin"), keys, KeyAliases.CACHE))
    val capture = LocalCaptureClock()
    val notices = mutableListOf<String>()
    val now: Instant = Instant.parse("2026-10-28T12:00:00Z")
    val api = PrivatePasteApi(server.url("/"), "tok", OkHttpClient())
    val vaultKey = VaultKey.create()

    /** Null: this phone has no vault key (locked). */
    @Volatile var codec: ClipCodec? = ClipCodec(vaultKey)
    @Volatile var limits = ServerLimits(textKb = 1024, imageMb = 20)
    val keyResets = AtomicInteger()

    /** The key ids the sender reported as reset, in order. */
    val resetKeyIds: MutableList<String> = java.util.concurrent.CopyOnWriteArrayList()
    val sender = sender()
    val imageDir = File(dir, "queue-images")

    fun sender(isEnding: () -> Boolean = { false }) = ClipSender(
        api, { codec }, { limits }, queue, cache, capture, { notices += it }, Clock.fixed(now, ZoneOffset.UTC),
        onKeyReset = { resetKeyIds += it; keyResets.incrementAndGet() }, isEnding = isEnding,
    )

    /** A clip as the server returns it: [text] sealed in its payload under [vaultKey]. */
    fun clipJson(id: String, text: String, hash: String = "h-$id", lastCopiedAt: String = "2026-10-28T12:00:05+00:00"): String {
        val json = ProtocolJson.json.encodeToString(mapOf("text" to text)).toByteArray()
        val payload = Base64.getEncoder().encodeToString(SealedBox.seal(vaultKey, "payload", json))
        return """{"id":"$id","seq":5,"kind":"text","hash":"$hash","sizeBytes":2,"sourceDeviceId":"d","sourceDeviceName":"Phone",""" +
            """"lastCopiedAt":"$lastCopiedAt","payload":"$payload","keyId":"${vaultKey.keyId}"}"""
    }

    /** Answers an upload with the clip the server would store from it. */
    fun echo(request: RecordedRequest, id: String = "c-${request.sequenceNumber}"): MockResponse {
        val meta = uploaded(request).meta
        return json(
            """{"id":"$id","seq":5,"kind":"${meta.kind}","hash":"${meta.hash}","sizeBytes":${meta.sizeBytes},""" +
                """"sourceDeviceId":"d","sourceDeviceName":"Phone","lastCopiedAt":"2026-10-28T12:00:05+00:00",""" +
                """"payload":"${meta.payload}","keyId":"${meta.keyId}"}""",
        )
    }

    /** Reads the multipart upload without consuming the recorded body. */
    fun uploaded(request: RecordedRequest): Upload {
        val boundary = request.getHeader("Content-Type")!!.toMediaType().parameter("boundary")!!
        val parts = HashMap<String, ByteArray>()
        MultipartReader(request.body.clone(), boundary).use { reader ->
            while (true) {
                val part = reader.nextPart() ?: break
                val name = Regex("name=\"([^\"]+)\"").find(part.headers["Content-Disposition"]!!)!!.groupValues[1]
                parts[name] = part.body.readByteArray()
            }
        }
        val meta = ProtocolJson.json.decodeFromString(ClipUploadMeta.serializer(), parts.getValue("meta").decodeToString())
        val text = if (meta.kind == "image") null else ClipCodec(vaultKey).decode(
            ClipDto(
                id = "x", seq = 1, kind = ClipKind.TEXT, hash = meta.hash, sizeBytes = meta.sizeBytes,
                lastCopiedAt = now, payload = meta.payload, keyId = meta.keyId,
            ),
        ).text
        return Upload(meta, text, parts)
    }

    fun json(body: String, code: Int = 200) =
        MockResponse().setResponseCode(code).setHeader("Content-Type", "application/json").setBody(body)

    fun problem(code: Int, detail: String, title: String = "t") =
        MockResponse().setResponseCode(code).setHeader("Content-Type", "application/problem+json")
            .setBody("""{"title":"$title","detail":"$detail"}""")

    /** Answers every request with [respond] until replaced. */
    fun respondWith(respond: (RecordedRequest) -> MockResponse) {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest) = respond(request)
        }
    }

    fun shutdown() = server.shutdown()

    companion object {
        /** A small real PNG (needs Robolectric's native graphics). */
        fun png(width: Int = 20, height: Int = 10): ByteArray {
            val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
            bitmap.eraseColor(Color.GREEN)
            return ByteArrayOutputStream().also { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }.toByteArray()
        }
    }
}
