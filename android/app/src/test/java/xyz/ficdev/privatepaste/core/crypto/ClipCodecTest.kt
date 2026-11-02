package xyz.ficdev.privatepaste.core.crypto

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import androidx.exifinterface.media.ExifInterface
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.GraphicsMode
import xyz.ficdev.privatepaste.core.ContentHash
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ClipUploadMeta
import xyz.ficdev.privatepaste.core.model.ServerLimits
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.ByteBuffer
import java.time.Instant
import java.util.Base64
import java.util.zip.CRC32
import kotlin.random.Random

@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class ClipCodecTest {

    @get:Rule val tmp = TemporaryFolder()

    private val vectors = Json.parseToJsonElement(File("../../docs/e2e-test-vectors.json").readText(Charsets.UTF_8)).jsonObject
    private fun vector(section: String, field: String) = vectors.getValue(section).jsonObject.getValue(field).jsonPrimitive.content
    private fun String.hexToBytes() = ByteArray(length / 2) { substring(it * 2, it * 2 + 2).toInt(16).toByte() }

    private val vectorKey = VaultKey.fromBytes(vector("vaultKey", "vk").hexToBytes())
    private val key = VaultKey.create()
    private val codec = ClipCodec(key)
    private val limits = ServerLimits(textKb = 1024, imageMb = 20)
    private val copiedAt = Instant.parse("2026-11-01T10:00:00Z")

    /** The DTO the server would send back for an upload. */
    private fun wire(meta: ClipUploadMeta, kind: ClipKind = ClipKind.entries.first { it.wire == meta.kind }) = ClipDto(
        id = "c1", seq = 1, kind = kind, hash = meta.hash, sizeBytes = meta.sizeBytes, lastCopiedAt = copiedAt,
        payload = meta.payload, keyId = meta.keyId, hasThumb = kind == ClipKind.IMAGE,
        blobCount = if (kind == ClipKind.IMAGE) 1 else 0,
    )

    @Test
    fun textRoundTrips() {
        val encoded = codec.encodeText("hello", copiedAt, backfill = true)
        val meta = encoded.meta
        assertEquals("text", meta.kind)
        assertEquals(key.keyId, meta.keyId)
        assertEquals(5L, meta.sizeBytes)
        assertEquals(copiedAt, meta.copiedAt)
        assertTrue(meta.backfill)
        assertNull(encoded.thumb)
        assertTrue(encoded.blobs.isEmpty())
        // The payload is sealed JSON with only the fields that are set.
        val json = SealedBox.open(key, "payload", Base64.getDecoder().decode(meta.payload)).toString(Charsets.UTF_8)
        assertEquals("""{"text":"hello"}""", json)

        val decoded = codec.decode(wire(meta))
        assertEquals(ClipKind.TEXT, decoded.kind)
        assertEquals("hello", decoded.text)
    }

    @Test
    fun keyedHashMatchesTheDotNetVector() {
        val meta = ClipCodec(vectorKey).encodeText(vector("keyedHash", "text"), null, backfill = false).meta
        assertEquals(vector("keyedHash", "keyedHash"), meta.hash)
        assertEquals(vector("vaultKey", "keyId"), meta.keyId)
    }

    @Test
    fun decodesTheDotNetPayloadVector() {
        val dto = ClipDto(
            id = "v", seq = 1, kind = ClipKind.TEXT, hash = "h", sizeBytes = 5, lastCopiedAt = copiedAt,
            payload = Base64.getEncoder().encodeToString(vector("payload", "sealed").hexToBytes()), keyId = vector("vaultKey", "keyId"),
        )
        assertEquals("hello", ClipCodec(vectorKey).decode(dto).text)
    }

    @Test
    fun linksAreClassifiedLikeTheDesktop() {
        assertEquals("link", codec.encodeText("https://example.com/a?b=c", null, false).meta.kind)
        assertEquals("link", codec.encodeText("  http://example.com  ", null, false).meta.kind)
        assertEquals("text", codec.encodeText("see https://example.com", null, false).meta.kind)
        assertEquals("text", codec.encodeText("ftp://example.com", null, false).meta.kind)
        assertEquals("text", codec.encodeText("example.com", null, false).meta.kind)
        assertEquals("text", codec.encodeText("", null, false).meta.kind)
    }

    @Test
    fun linksMatchTheSharedClassifierVectors() {
        val cases = Json.parseToJsonElement(File("../../docs/classifier-test-vectors.json").readText(Charsets.UTF_8))
            .jsonObject.getValue("cases").jsonArray
        val wrong = cases.map { it.jsonObject }
            .filter { ClipCodec.isLink(it.getValue("text").jsonPrimitive.content) != it.getValue("link").jsonPrimitive.boolean }
            .map { it.getValue("text").jsonPrimitive.content }
        assertEquals(emptyList<String>(), wrong)
    }

    @Test
    fun unknownKindForeignKeyAndBadPayloadDecodeAsUnknown() {
        val meta = codec.encodeText("secret", null, false).meta
        // An unknown kind keeps none of the server's own fields, so search can't match them.
        val newerKind = codec.decode(wire(meta, ClipKind.UNKNOWN).copy(text = "server text", width = 3, height = 4))
        assertEquals(ClipKind.UNKNOWN, newerKind.kind)
        assertNull(newerKind.text)
        assertNull(newerKind.width)
        assertNull(newerKind.height)

        val foreign = ClipCodec(VaultKey.create()).decode(wire(meta))
        assertEquals(ClipKind.UNKNOWN, foreign.kind)
        assertNull(foreign.text)

        assertEquals(ClipKind.UNKNOWN, codec.decode(wire(meta).copy(payload = null)).kind)
        assertEquals(ClipKind.UNKNOWN, codec.decode(wire(meta).copy(payload = "not base64!")).kind)
        val tampered = Base64.getDecoder().decode(meta.payload).also { it[20] = (it[20].toInt() xor 1).toByte() }
        assertEquals(ClipKind.UNKNOWN, codec.decode(wire(meta).copy(payload = Base64.getEncoder().encodeToString(tampered))).kind)
        // A text clip whose payload has no text is broken too.
        val empty = Base64.getEncoder().encodeToString(SealedBox.seal(key, "payload", "{}".toByteArray()))
        assertEquals(ClipKind.UNKNOWN, codec.decode(wire(meta).copy(payload = empty)).kind)
        // Fields from a newer release are ignored.
        val newer = Base64.getEncoder().encodeToString(SealedBox.seal(key, "payload", """{"text":"x","html":"<b>x</b>"}""".toByteArray()))
        assertEquals("x", codec.decode(wire(meta).copy(payload = newer)).text)
    }

    @Test
    fun exifRotatedJpegComesOutUprightWithAThumbnailFitting256() {
        // 600x300, left half red and right half blue, tagged "rotate 90 clockwise".
        val jpeg = jpegWithOrientation(600, 300, ExifInterface.ORIENTATION_ROTATE_90)
        val encoded = codec.encodeImage(jpeg, copiedAt, backfill = false, limits)
        assertEquals("image", encoded.meta.kind)

        val decoded = codec.decode(wire(encoded.meta))
        assertEquals(ClipKind.IMAGE, decoded.kind)
        assertEquals(300, decoded.width)
        assertEquals(600, decoded.height)

        val png = codec.decodeBlob(encoded.blobs.single())
        assertEquals(png.size.toLong(), encoded.meta.sizeBytes)
        val upright = BitmapFactory.decodeByteArray(png, 0, png.size)
        assertEquals(300, upright.width)
        assertEquals(600, upright.height)
        // Rotated clockwise, the left (red) half is now on top.
        assertTrue(Color.red(upright.getPixel(150, 100)) > 200 && Color.blue(upright.getPixel(150, 100)) < 60)
        assertTrue(Color.blue(upright.getPixel(150, 500)) > 200 && Color.red(upright.getPixel(150, 500)) < 60)

        val thumbPng = codec.decodeThumb(encoded.thumb!!)
        val thumb = BitmapFactory.decodeByteArray(thumbPng, 0, thumbPng.size)
        assertTrue(thumb.width <= 256 && thumb.height <= 256)
        assertEquals(256, thumb.height)
        assertEquals(128, thumb.width)
    }

    @Test
    fun theHashIsOfTheUprightPixels() {
        val rotated = codec.encodeImage(jpegWithOrientation(40, 20, ExifInterface.ORIENTATION_ROTATE_90), null, false, limits)
        val png = codec.decodeBlob(rotated.blobs.single())
        // Uploading the upright PNG itself gives the same hash: every client hashes what it puts on the clipboard.
        assertEquals(rotated.meta.hash, codec.encodeImage(png, null, false, limits).meta.hash)
    }

    @Test
    fun rejects51Megapixels() {
        val e = assertThrows(ClipTooLargeException::class.java) { codec.encodeImage(pngHeader(7200, 7200), null, false, limits) }
        assertEquals("Images over 50 megapixels aren't synced.", e.message)
    }

    @Test
    fun rejectsAPngOverTheServersLimit() {
        val noise = Bitmap.createBitmap(800, 800, Bitmap.Config.ARGB_8888)
        val random = Random(7)
        for (y in 0 until 800) for (x in 0 until 800) noise.setPixel(x, y, random.nextInt() or (0xFF shl 24))
        val png = ByteArrayOutputStream().also { noise.compress(Bitmap.CompressFormat.PNG, 100, it) }.toByteArray()
        assertTrue(png.size > 1024 * 1024)
        val e = assertThrows(ClipTooLargeException::class.java) { codec.encodeImage(png, null, false, ServerLimits(1024, 1)) }
        assertEquals("Images over 1 MB aren't synced.", e.message)
    }

    @Test
    fun undecodableBytesAreUnreadable() {
        assertThrows(UnreadableImageException::class.java) { codec.encodeImage(byteArrayOf(1, 2, 3), null, false, limits) }
    }

    @Test
    fun thumbAndBlobRejectAnotherKey() {
        val encoded = codec.encodeImage(jpegWithOrientation(20, 20, ExifInterface.ORIENTATION_NORMAL), null, false, limits)
        val other = ClipCodec(VaultKey.create())
        assertThrows(java.security.GeneralSecurityException::class.java) { other.decodeThumb(encoded.thumb!!) }
        assertThrows(java.security.GeneralSecurityException::class.java) { other.decodeBlob(encoded.blobs.single()) }
        assertFalse(encoded.meta.payload.isEmpty())
    }

    private fun jpegWithOrientation(width: Int, height: Int, orientation: Int): ByteArray {
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        for (y in 0 until height) for (x in 0 until width) bitmap.setPixel(x, y, if (x < width / 2) Color.RED else Color.BLUE)
        val file = tmp.newFile()
        file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.JPEG, 95, it) }
        ExifInterface(file).apply {
            setAttribute(ExifInterface.TAG_ORIENTATION, orientation.toString())
            saveAttributes()
        }
        return file.readBytes()
    }

    /** A PNG that only declares its size: enough for the bounds check, which must reject it before decoding. */
    private fun pngHeader(width: Int, height: Int): ByteArray {
        val out = ByteArrayOutputStream()
        out.write(byteArrayOf(0x89.toByte(), 'P'.code.toByte(), 'N'.code.toByte(), 'G'.code.toByte(), 13, 10, 26, 10))
        fun chunk(type: String, data: ByteArray) {
            out.write(ByteBuffer.allocate(4).putInt(data.size).array())
            val typed = type.toByteArray() + data
            out.write(typed)
            out.write(ByteBuffer.allocate(4).putInt(CRC32().apply { update(typed) }.value.toInt()).array())
        }
        chunk("IHDR", ByteBuffer.allocate(13).putInt(width).putInt(height).put(8).put(6).put(0).put(0).put(0).array())
        chunk("IDAT", byteArrayOf(0x78, 0x9c.toByte(), 0x03, 0x00, 0x00, 0x00, 0x00, 0x01))
        chunk("IEND", ByteArray(0))
        return out.toByteArray()
    }

    @Test
    fun theWireCarriesTheKeyedHashNotThePlainOne() {
        val meta = codec.encodeText("hello", null, false).meta
        assertEquals(KeyedHash.forHash(key, ContentHash.text("hello")), meta.hash)
        assertEquals(meta.hash, codec.keyedHash(ContentHash.text("hello")))
    }

    @Test
    fun aClosedKeyIsNotABadClip() {
        val meta = codec.encodeText("fine", null, false).meta
        key.close()
        // Not UNKNOWN: the clip is fine, the key is gone. The caller must not cache anything for it.
        assertThrows(VaultKeyClosedException::class.java) { codec.decode(wire(meta)) }
        assertThrows(VaultKeyClosedException::class.java) { codec.encodeText("x", null, false) }
    }

    private fun outgoing(name: String, content: ByteArray, mime: String = "application/octet-stream"): OutgoingFile {
        val file = tmp.newFile()
        file.writeBytes(content)
        return OutgoingFile(file, name, content.size.toLong(), mime, ContentHash.sha256Hex(content))
    }

    @Test
    fun filesRoundTripWithOneBlobEachInOrder() {
        val big = Random(7).nextBytes(BlobCipher.CHUNK_SIZE + 10)   // two chunks
        val files = listOf(outgoing("a.txt", "hello".toByteArray(), "text/plain"), outgoing("b.bin", ByteArray(0)), outgoing("c.dat", big))
        val sealedDir = tmp.newFolder()

        val encoded = codec.encodeFiles(files, sealedDir, copiedAt = null, backfill = false)

        val meta = encoded.meta
        assertEquals("file", meta.kind)
        assertEquals(5L + 0 + big.size, meta.sizeBytes)
        assertNull(meta.copiedAt)
        val digests = files.map { ContentHash.FileDigest(it.name, it.size, it.sha256Hex) }
        assertEquals(codec.keyedHash(ContentHash.files(digests)), meta.hash)
        // The payload holds only the file list, in blob order, with the .NET wire names.
        val json = SealedBox.open(key, "payload", Base64.getDecoder().decode(meta.payload)).toString(Charsets.UTF_8)
        assertEquals(
            """{"files":[{"name":"a.txt","size":5,"mime":"text/plain"},{"name":"b.bin","size":0,"mime":"application/octet-stream"},""" +
                """{"name":"c.dat","size":${big.size},"mime":"application/octet-stream"}]}""",
            json,
        )
        assertEquals(3, encoded.blobs.size)
        assertTrue("sealed blobs are written where they were asked", encoded.blobs.all { it.parentFile == sealedDir })
        assertTrue("an empty file still has a blob (one empty final chunk)", encoded.blobs[1].length() > 0)

        val decoded = codec.decode(wire(meta, ClipKind.FILE).copy(blobCount = 3))
        assertEquals(ClipKind.FILE, decoded.kind)
        assertNull(decoded.text)
        assertEquals(listOf("a.txt", "b.bin", "c.dat"), decoded.files!!.map { it.name })
        assertEquals(listOf(5L, 0L, big.size.toLong()), decoded.files!!.map { it.size })
        assertEquals("hello", codec.decodeBlob(encoded.blobs[0].readBytes()).toString(Charsets.UTF_8))
        assertEquals(0, codec.decodeBlob(encoded.blobs[1].readBytes()).size)
        val out = ByteArrayOutputStream()
        codec.decodeBlobTo(encoded.blobs[2].readBytes(), out)
        assertTrue(big.contentEquals(out.toByteArray()))
    }

    @Test
    fun aFileClipWithoutAMatchingFileListIsUnknown() {
        val meta = codec.encodeFiles(listOf(outgoing("a.txt", byteArrayOf(1))), tmp.newFolder(), null, false).meta
        assertEquals(ClipKind.UNKNOWN, codec.decode(wire(meta, ClipKind.FILE).copy(blobCount = 2)).kind)
        val noFiles = Base64.getEncoder().encodeToString(SealedBox.seal(key, "payload", """{"text":"x"}""".toByteArray()))
        assertEquals(ClipKind.UNKNOWN, codec.decode(wire(meta, ClipKind.FILE).copy(blobCount = 0, payload = noFiles)).kind)
        // A text clip never carries a file list, whatever its payload says.
        val textWithFiles = Base64.getEncoder().encodeToString(
            SealedBox.seal(key, "payload", """{"text":"x","files":[{"name":"a","size":1,"mime":"m"}]}""".toByteArray()),
        )
        val text = codec.decode(wire(meta, ClipKind.TEXT).copy(payload = textWithFiles))
        assertEquals("x", text.text)
        assertNull(text.files)
    }

    @Test
    fun aFileListWithoutMimeStillOpens() {
        val meta = codec.encodeFiles(listOf(outgoing("a.txt", byteArrayOf(1))), tmp.newFolder(), null, false).meta
        val payload = Base64.getEncoder().encodeToString(SealedBox.seal(key, "payload",
            """{"files":[{"name":"a.txt","size":1},{"name":"b","size":2,"mime":null}]}""".toByteArray()))

        val decoded = codec.decode(wire(meta, ClipKind.FILE).copy(blobCount = 2, payload = payload))

        assertEquals(ClipKind.FILE, decoded.kind)
        assertEquals(listOf("a.txt", "b"), decoded.files!!.map { it.name })
        assertEquals("application/octet-stream", decoded.files!![0].mime)
    }
}
