package xyz.ficdev.privatepaste.core.model

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class ProtocolJsonTest {

    private val json = ProtocolJson.json

    private val linkClipJson = """
        {"id":"c1","seq":42,"kind":"link","text":"https://example.com","hash":"abc123",
         "width":null,"height":null,"sizeBytes":19,"sourceDeviceId":"d1",
         "sourceDeviceName":"Desk","lastCopiedAt":"2026-10-28T10:00:00+00:00"}
    """.trimIndent()

    @Test
    fun parsesReadmeClipShape() {
        val clip = json.decodeFromString<ClipDto>(linkClipJson)

        assertEquals("c1", clip.id)
        assertEquals(42L, clip.seq)
        assertEquals(ClipKind.LINK, clip.kind)
        assertEquals("https://example.com", clip.text)
        assertEquals("abc123", clip.hash)
        assertNull(clip.width)
        assertNull(clip.height)
        assertEquals(19L, clip.sizeBytes)
        assertEquals("d1", clip.sourceDeviceId)
        assertEquals("Desk", clip.sourceDeviceName)
        assertEquals(Instant.parse("2026-10-28T10:00:00Z"), clip.lastCopiedAt)
    }

    @Test
    fun clipDto_withoutSourceUserName_decodes() {
        assertNull(json.decodeFromString<ClipDto>(linkClipJson).sourceUserName)
        assertNull(json.decodeFromString<ClipDto>(linkClipJson.replace("\"width\"", "\"sourceUserName\":null,\"width\"")).sourceUserName)
    }

    @Test
    fun clipDto_withSourceUserName_decodes() {
        val clip = json.decodeFromString<ClipDto>(linkClipJson.replace("\"width\"", "\"sourceUserName\":\"Ana\",\"width\""))
        assertEquals("Ana", clip.sourceUserName)
        assertEquals(clip, json.decodeFromString<ClipDto>(json.encodeToString(ClipDto.serializer(), clip)))
    }

    @Test
    fun pinnedAtDecodesWhenPresentNullOrMissing() {
        assertNull(json.decodeFromString<ClipDto>(linkClipJson).pinnedAt)
        val pinned = json.decodeFromString<ClipDto>(linkClipJson.replace("\"width\"", "\"pinnedAt\":\"2026-10-29T09:00:00+00:00\",\"width\""))
        assertEquals("2026-10-29T09:00:00+00:00", pinned.pinnedAt)
        // The server sends an explicit null for an unpinned clip.
        assertNull(json.decodeFromString<ClipDto>(linkClipJson.replace("\"width\"", "\"pinnedAt\":null,\"width\"")).pinnedAt)
    }

    @Test
    fun parsesImageClipWithNullTextAndAllKinds() {
        val clip = json.decodeFromString<ClipDto>(
            """{"id":"c2","seq":1,"kind":"image","text":null,"hash":"h","width":640,"height":480,
               "sizeBytes":1000,"sourceDeviceId":"d2","sourceDeviceName":null,
               "lastCopiedAt":"2026-10-28T10:00:00Z"}""",
        )
        assertEquals(ClipKind.IMAGE, clip.kind)
        assertNull(clip.text)
        assertEquals(640, clip.width)
        assertEquals(480, clip.height)
        assertNull(clip.sourceDeviceName)
        assertEquals(ClipKind.TEXT, json.decodeFromString<ClipKind>("\"text\""))
    }

    @Test
    fun sourceDevicePlatformIsOptional() {
        // Older servers (and older caches) have no sourceDevicePlatform: it decodes as null.
        val without = json.decodeFromString<ClipDto>(linkClipJson)
        assertNull(without.sourceDevicePlatform)
        assertEquals(without, json.decodeFromString<ClipDto>(json.encodeToString(ClipDto.serializer(), without)))

        val withPlatform = json.decodeFromString<ClipDto>(
            linkClipJson.replace("\"sourceDeviceName\":\"Desk\",", "\"sourceDeviceName\":\"Desk\",\"sourceDevicePlatform\":\"windows\","),
        )
        assertEquals("windows", withPlatform.sourceDevicePlatform)
        val encoded = json.encodeToString(ClipDto.serializer(), withPlatform)
        assertTrue(encoded.contains("\"sourceDevicePlatform\":\"windows\""))
        assertEquals(withPlatform, json.decodeFromString<ClipDto>(encoded))

        val explicitNull = json.decodeFromString<ClipDto>(
            linkClipJson.replace("\"sourceDeviceName\":\"Desk\",", "\"sourceDeviceName\":\"Desk\",\"sourceDevicePlatform\":null,"),
        )
        assertNull(explicitNull.sourceDevicePlatform)
    }

    /** A clip copied or restored in the web UI, exactly as the server sends it: no source device id. */
    private val webClipJson = """
        {"id":"w1","seq":43,"kind":"text","text":"from the browser","hash":"wh","width":null,"height":null,
         "sizeBytes":16,"sourceDeviceId":null,"sourceDeviceName":"Web","sourceDevicePlatform":"web",
         "lastCopiedAt":"2026-10-28T10:05:00+00:00"}
    """.trimIndent()

    @Test
    fun webClipWithNullSourceDeviceIdDecodes() {
        val clip = json.decodeFromString<ClipDto>(webClipJson)
        assertNull(clip.sourceDeviceId)
        assertEquals("Web", clip.sourceDeviceName)
        assertEquals("web", clip.sourceDevicePlatform)
        assertEquals(clip, json.decodeFromString<ClipDto>(json.encodeToString(ClipDto.serializer(), clip)))
        // An absent id decodes the same way.
        assertNull(json.decodeFromString<ClipDto>(webClipJson.replace("\"sourceDeviceId\":null,", "")).sourceDeviceId)
    }

    @Test
    fun syncPageWithAWebClipDecodes() {
        val page = json.decodeFromString<SyncPage>(
            """{"clips":[$linkClipJson,$webClipJson],"deletedIds":[],"latestSeq":43,"hasMore":false}""",
        )
        assertEquals(listOf("c1", "w1"), page.clips.map { it.id })
        assertNull(page.clips[1].sourceDeviceId)
    }

    @Test
    fun parseWsWebClipCreated() {
        val event = ProtocolJson.parseWs("""{"type":"clip.created","clip":$webClipJson,"live":true}""")
        assertTrue(event is WsEvent.ClipCreated)
        event as WsEvent.ClipCreated
        assertEquals("w1", event.clip.id)
        assertNull(event.clip.sourceDeviceId)
        assertEquals(true, event.live)
    }

    @Test
    fun ignoresUnknownFields() {
        val withExtra = linkClipJson.replaceFirst("{", """{"futureField":{"nested":[1,2]},"other":true,""")
        val clip = json.decodeFromString<ClipDto>(withExtra)
        assertEquals("c1", clip.id)

        val page = json.decodeFromString<SyncPage>(
            """{"clips":[$linkClipJson],"deletedIds":["x"],"latestSeq":50,"hasMore":false,"extra":1}""",
        )
        assertEquals(1, page.clips.size)
        assertEquals(listOf("x"), page.deletedIds)
        assertEquals(50L, page.latestSeq)
        assertEquals(false, page.hasMore)
    }

    @Test
    fun parsesClipPageAndLoginResponse() {
        val page = json.decodeFromString<ClipPage>("""{"items":[$linkClipJson],"nextCursor":null}""")
        assertEquals(1, page.items.size)
        assertNull(page.nextCursor)

        val login = json.decodeFromString<LoginResponse>("""{"deviceId":"d1","token":"t"}""")
        assertEquals(LoginResponse("d1", "t"), login)
    }

    @Test
    fun parseWsClipCreated() {
        val event = ProtocolJson.parseWs("""{"type":"clip.created","clip":$linkClipJson,"live":true}""")
        assertTrue(event is WsEvent.ClipCreated)
        event as WsEvent.ClipCreated
        assertEquals("c1", event.clip.id)
        assertEquals(true, event.live)

        val backfilled = ProtocolJson.parseWs("""{"type":"clip.created","clip":$linkClipJson,"live":false}""")
        assertEquals(false, (backfilled as WsEvent.ClipCreated).live)
    }

    @Test
    fun parseWsInvalidLiveIsNotLive() {
        for (live in listOf("null", "\"yes\"", "\"true\"", "1", "{}")) {
            val event = ProtocolJson.parseWs("""{"type":"clip.created","clip":$linkClipJson,"live":$live}""")
            assertEquals(live, WsEvent.ClipCreated(json.decodeFromString<ClipDto>(linkClipJson), false), event)
        }
    }

    @Test
    fun parseWsNonObjectClipIsUnknown() {
        for (clip in listOf("null", "\"c1\"", "42", "[]")) {
            assertEquals(clip, WsEvent.Unknown, ProtocolJson.parseWs("""{"type":"clip.created","clip":$clip,"live":true}"""))
        }
    }

    @Test
    fun parseWsClipDeleted() {
        assertEquals(
            WsEvent.ClipDeleted("c9", 123L),
            ProtocolJson.parseWs("""{"type":"clip.deleted","id":"c9","seq":123}"""),
        )
    }

    @Test
    fun parseWsPong() {
        assertEquals(WsEvent.Pong, ProtocolJson.parseWs("""{"type":"pong"}"""))
    }

    @Test
    fun parseWsOtherTypesAreUnknown() {
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs("""{"type":"device.renamed","id":"d1"}"""))
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs("""{"id":"no-type"}"""))
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs("""{"type":"clip.created","live":true}"""))
    }

    @Test
    fun parseWsMalformedIsUnknown() {
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs("not json"))
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs("{\"type\":"))
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs("[1,2,3]"))
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs(""))
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs("""{"type":"clip.deleted","id":"c9","seq":"abc"}"""))
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs("""{"type":"clip.deleted","seq":1}"""))
        val badTime = linkClipJson.replace("2026-10-28T10:00:00+00:00", "yesterday")
        assertEquals(WsEvent.Unknown, ProtocolJson.parseWs("""{"type":"clip.created","clip":$badTime,"live":true}"""))
    }

    @Test
    fun instantRoundTripsWithOffsetAndZulu() {
        val expected = Instant.parse("2026-10-28T10:00:00Z")
        for (raw in listOf("2026-10-28T10:00:00+00:00", "2026-10-28T10:00:00Z", "2026-10-28T12:00:00+02:00")) {
            val clip = json.decodeFromString<ClipDto>(linkClipJson.replace("2026-10-28T10:00:00+00:00", raw))
            assertEquals(raw, expected, clip.lastCopiedAt)

            val encoded = json.encodeToString(ClipDto.serializer(), clip)
            val encodedTime = Json.parseToJsonElement(encoded).jsonObject["lastCopiedAt"]!!.jsonPrimitive.content
            assertEquals("2026-10-28T10:00:00Z", encodedTime)
            assertEquals(clip, json.decodeFromString<ClipDto>(encoded))
        }
    }

    @Test
    fun instantKeepsDotNetFractionalSeconds() {
        val clip = json.decodeFromString<ClipDto>(
            linkClipJson.replace("2026-10-28T10:00:00+00:00", "2026-10-28T10:00:00.1234567+00:00"),
        )
        assertEquals(Instant.parse("2026-10-28T10:00:00.123456700Z"), clip.lastCopiedAt)
    }

    @Test
    fun encodingOmitsNulls() {
        val clip = json.decodeFromString<ClipDto>(linkClipJson)
        val encoded = Json.parseToJsonElement(json.encodeToString(ClipDto.serializer(), clip)).jsonObject
        assertTrue("width" !in encoded)
        assertTrue("height" !in encoded)
        assertEquals("link", encoded["kind"]!!.jsonPrimitive.content)
    }
}
