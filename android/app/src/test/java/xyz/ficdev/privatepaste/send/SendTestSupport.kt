package xyz.ficdev.privatepaste.send

import android.app.Application
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.storage.CryptoFile
import xyz.ficdev.privatepaste.storage.HistoryCache
import xyz.ficdev.privatepaste.storage.InMemoryKeyProvider
import xyz.ficdev.privatepaste.storage.KeyAliases
import xyz.ficdev.privatepaste.storage.OutboundQueue
import xyz.ficdev.privatepaste.sync.LocalCaptureClock
import java.io.File
import java.time.Clock
import java.time.Instant
import java.time.ZoneOffset

/** Application that hands activities under test a [ClipSender] (set [sender] before launching). */
class TestSenderApp : Application(), SenderProvider {
    override val clipSender: ClipSender? get() = sender
    override val sendScope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    companion object {
        @Volatile var sender: ClipSender? = null
    }
}

/** A ClipSender wired to a MockWebServer and real (temp-dir) storage. */
class SenderFixture(dir: File) {
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
    val sender = ClipSender(api, queue, cache, capture, { notices += it }, Clock.fixed(now, ZoneOffset.UTC))
    val imageDir = File(dir, "queue-images")

    fun clipJson(id: String, text: String, hash: String = "h-$id", lastCopiedAt: String = "2026-10-28T12:00:05+00:00") =
        """{"id":"$id","seq":5,"kind":"text","text":"$text","hash":"$hash","sizeBytes":2,"sourceDeviceId":"d","sourceDeviceName":"Phone","lastCopiedAt":"$lastCopiedAt"}"""

    fun json(body: String, code: Int = 200) =
        MockResponse().setResponseCode(code).setHeader("Content-Type", "application/json").setBody(body)

    fun problem(code: Int, detail: String) =
        MockResponse().setResponseCode(code).setHeader("Content-Type", "application/problem+json")
            .setBody("""{"title":"t","detail":"$detail"}""")

    /** Answers every request with [respond] until replaced. */
    fun respondWith(respond: (RecordedRequest) -> MockResponse) {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest) = respond(request)
        }
    }

    fun shutdown() = server.shutdown()
}
