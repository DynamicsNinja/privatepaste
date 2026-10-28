package xyz.ficdev.privatepaste.core.model

import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerialName
import kotlinx.serialization.SerializationException
import kotlinx.serialization.Serializable
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import java.time.Instant
import java.time.OffsetDateTime
import java.time.format.DateTimeParseException
import kotlin.coroutines.cancellation.CancellationException

// Wire models for the server API (README.md "API" section). JSON is camelCase and enums are lowercase.

@Serializable
enum class ClipKind {
    @SerialName("text") TEXT,
    @SerialName("link") LINK,
    @SerialName("image") IMAGE,
}

@Serializable
data class ClipDto(
    val id: String,
    val seq: Long,
    val kind: ClipKind,
    val text: String? = null,
    val hash: String,
    val width: Int? = null,
    val height: Int? = null,
    val sizeBytes: Long,
    val sourceDeviceId: String,
    val sourceDeviceName: String? = null,
    /** Lowercase platform of the source device ("windows", "macos", "linux", "android", "web", ...); null if unknown. */
    val sourceDevicePlatform: String? = null,
    @Serializable(InstantSerializer::class) val lastCopiedAt: Instant,
)

@Serializable
data class ClipPage(val items: List<ClipDto>, val nextCursor: String? = null)

@Serializable
data class SyncPage(val clips: List<ClipDto>, val deletedIds: List<String>, val latestSeq: Long, val hasMore: Boolean)

@Serializable
data class LoginResponse(val deviceId: String, val token: String)

/** A message received on the `/ws` WebSocket. */
sealed interface WsEvent {
    data class ClipCreated(val clip: ClipDto, val live: Boolean) : WsEvent
    data class ClipDeleted(val id: String, val seq: Long) : WsEvent
    data object Pong : WsEvent

    /** Any other message type, or a message that could not be parsed. */
    data object Unknown : WsEvent
}

/**
 * ISO 8601 timestamps. Reads any offset (.NET writes `2026-10-28T10:00:00+00:00`) or a `Z` suffix,
 * and writes UTC with a `Z` suffix.
 */
object InstantSerializer : KSerializer<Instant> {
    override val descriptor: SerialDescriptor =
        PrimitiveSerialDescriptor("xyz.ficdev.privatepaste.Instant", PrimitiveKind.STRING)

    override fun deserialize(decoder: Decoder): Instant {
        val text = decoder.decodeString()
        return try {
            OffsetDateTime.parse(text).toInstant()
        } catch (e: DateTimeParseException) {
            throw SerializationException("Invalid ISO 8601 timestamp: $text", e)
        }
    }

    override fun serialize(encoder: Encoder, value: Instant) = encoder.encodeString(value.toString())
}

object ProtocolJson {
    val json: Json = Json {
        ignoreUnknownKeys = true
        explicitNulls = false
    }

    /** Parses a WebSocket text frame. Never throws: unknown or malformed messages give [WsEvent.Unknown]. */
    fun parseWs(text: String): WsEvent = try {
        val obj = json.parseToJsonElement(text).jsonObject
        when (obj.string("type")) {
            "clip.created" -> WsEvent.ClipCreated(
                clip = json.decodeFromJsonElement(ClipDto.serializer(), obj.getValue("clip")),
                // Only a real JSON boolean `true` is live; null, strings or other shapes mean history only.
                live = (obj["live"] as? JsonPrimitive)?.takeUnless { it.isString }?.booleanOrNull ?: false,
            )
            "clip.deleted" -> WsEvent.ClipDeleted(
                id = obj.string("id") ?: return WsEvent.Unknown,
                seq = obj.getValue("seq").jsonPrimitive.long,
            )
            "pong" -> WsEvent.Pong
            else -> WsEvent.Unknown
        }
    } catch (e: CancellationException) {
        throw e
    } catch (_: Exception) {
        // Malformed JSON, a non-object frame, missing or mistyped fields and failed clip decoding
        // (IllegalArgumentException, IllegalStateException, NoSuchElementException, ...).
        WsEvent.Unknown
    }

    private fun JsonObject.string(key: String): String? =
        (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.content
}
