package xyz.ficdev.privatepaste.files

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import xyz.ficdev.privatepaste.core.ContentHash
import xyz.ficdev.privatepaste.core.crypto.ClipCodec
import xyz.ficdev.privatepaste.core.crypto.OutgoingFile
import xyz.ficdev.privatepaste.core.crypto.VaultKey
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import java.io.File
import java.security.GeneralSecurityException
import java.time.Instant

class ClipFileStoreTest {
    @get:Rule val tmp = TemporaryFolder()

    private val codec = ClipCodec(VaultKey.create())

    /** A file clip as the cache holds it (opened) plus its sealed blobs, as the server would serve them. */
    private fun fileClip(id: String, vararg files: Pair<String, ByteArray>): Pair<ClipDto, List<ByteArray>> {
        val src = tmp.newFolder()
        val outgoing = files.mapIndexed { i, (name, bytes) ->
            val f = File(src, "in$i").apply { writeBytes(bytes) }
            OutgoingFile(f, name, bytes.size.toLong(), "application/octet-stream", ContentHash.sha256Hex(bytes))
        }
        val encoded = codec.encodeFiles(outgoing, tmp.newFolder(), null, false)
        val wire = ClipDto(
            id = id, seq = 1, kind = ClipKind.FILE, hash = encoded.meta.hash, sizeBytes = encoded.meta.sizeBytes,
            lastCopiedAt = Instant.parse("2026-11-02T10:00:00Z"), payload = encoded.meta.payload, keyId = encoded.meta.keyId,
            blobCount = files.size,
        )
        return codec.decode(wire) to encoded.blobs.map { it.readBytes() }
    }

    @Test
    fun hostileNamesStayInside() = runBlocking {
        val root = tmp.newFolder("files")
        val store = ClipFileStore(root)
        val (clip, blobs) = fileClip(
            "c/../1",
            "../../evil.exe" to byteArrayOf(1), "C:\\x\\a.txt" to byteArrayOf(2), "CON.txt" to byteArrayOf(3),
            "a<b>.txt" to byteArrayOf(4), "report.pdf" to byteArrayOf(5), "sub/report.pdf" to byteArrayOf(6),
        )

        val files = store.materialize(clip, { blobs[it] }, codec)

        assertEquals(listOf("evil.exe", "a.txt", "_CON.txt", "ab.txt", "report.pdf", "report (2).pdf"), files.map { it.name })
        val clipDir = files.first().parentFile!!.canonicalFile
        assertEquals("one folder per clip, right under the root", root.canonicalFile, clipDir.parentFile)
        assertTrue(files.all { it.canonicalFile.parentFile == clipDir })
        assertEquals((1..6).toList(), files.map { it.readBytes().single().toInt() })
        assertFalse(File(root.parentFile, "evil.exe").exists())
    }

    @Test
    fun aTempNameNeverOverwritesAnotherFileOfTheClip() = runBlocking {
        val store = ClipFileStore(tmp.newFolder("files"))
        for (order in listOf(listOf("a.txt.part", "a.txt"), listOf("a.txt", "a.txt.part"))) {
            val (clip, blobs) = fileClip("c-${order.first()}", order[0] to byteArrayOf(1), order[1] to byteArrayOf(2))

            val files = store.materialize(clip, { blobs[it] }, codec)

            assertEquals(2, files.map { it.name.lowercase() }.toSet().size)
            assertEquals(listOf(1, 2), files.map { it.readBytes().single().toInt() })
            assertEquals(files.map { it.name }.toSet(), files.first().parentFile!!.list()!!.toSet())
        }
    }

    @Test
    fun aMaterializedClipIsNotFetchedAgain() = runBlocking {
        val store = ClipFileStore(tmp.newFolder("files"))
        val (clip, blobs) = fileClip("c1", "a.txt" to "hello".toByteArray())
        var fetches = 0
        val first = store.materialize(clip, { fetches++; blobs[it] }, codec)
        val second = store.materialize(clip, { fetches++; blobs[it] }, codec)

        assertEquals(first, second)
        assertEquals(1, fetches)
        assertArrayEquals("hello".toByteArray(), second.single().readBytes())
    }

    @Test
    fun failedDecryptLeavesNothing() = runBlocking {
        val root = tmp.newFolder("files")
        val store = ClipFileStore(root)
        val big = ByteArray(1024 * 1024 + 100) { it.toByte() }   // two chunks: the first one authenticates and is written
        val (clip, blobs) = fileClip("c1", "ok.txt" to byteArrayOf(1, 2), "big.bin" to big)
        val tampered = blobs[1].copyOf().also { it[it.size - 1] = (it[it.size - 1].toInt() xor 1).toByte() }

        try {
            store.materialize(clip, { if (it == 1) tampered else blobs[it] }, codec)
            fail("expected the tampered blob to fail")
        } catch (_: GeneralSecurityException) {
        }
        assertEquals("no file and no .part is left", emptyList<String>(), root.walkTopDown().filter { it.isFile }.map { it.name }.toList())

        // A retry downloads again and succeeds.
        val files = store.materialize(clip, { blobs[it] }, codec)
        assertArrayEquals(big, files[1].readBytes())
    }

    @Test
    fun failedFetchLeavesNothing() = runBlocking {
        val root = tmp.newFolder("files")
        val store = ClipFileStore(root)
        val (clip, blobs) = fileClip("c1", "a.txt" to byteArrayOf(1), "b.txt" to byteArrayOf(2))
        try {
            store.materialize(clip, { if (it == 1) throw java.io.IOException("offline") else blobs[it] }, codec)
            fail("expected the fetch to fail")
        } catch (_: java.io.IOException) {
        }
        assertEquals(emptyList<String>(), root.walkTopDown().filter { it.isFile }.map { it.name }.toList())
    }

    @Test
    fun pruneKeepsOnlyCachedClips() = runBlocking {
        val root = tmp.newFolder("files")
        val store = ClipFileStore(root)
        val (a, blobsA) = fileClip("a", "a.txt" to byteArrayOf(1))
        val (b, blobsB) = fileClip("b", "b.txt" to byteArrayOf(2))
        val keptA = store.materialize(a, { blobsA[it] }, codec)
        val goneB = store.materialize(b, { blobsB[it] }, codec)
        File(root, "stray.part").writeBytes(byteArrayOf(0))

        store.prune(setOf("a"))

        assertTrue(keptA.single().exists())
        assertFalse(goneB.single().exists())
        assertFalse(goneB.single().parentFile!!.exists())
        assertFalse(File(root, "stray.part").exists())

        store.clear()
        assertFalse(keptA.single().exists())
    }

    @Test
    fun pruneKeepsTheLastClipsInUseEvenWhenNotCached() = runBlocking {
        val store = ClipFileStore(tmp.newFolder("files"))
        val opened = listOf("a", "b", "c").associateWith { id ->
            val (clip, blobs) = fileClip(id, "$id.txt" to byteArrayOf(1))
            store.markInUse(id)
            store.materialize(clip, { blobs[it] }, codec).single()
        }

        store.prune(emptySet())   // none is cached (older pages, the Pinned tab, a search of older clips)

        assertFalse("only the last ones in use stay", opened.getValue("a").exists())
        assertTrue(opened.getValue("b").exists())
        assertTrue(opened.getValue("c").exists())
    }

    @Test
    fun aClipWithoutFilesMaterializesNothing() = runBlocking {
        val store = ClipFileStore(tmp.newFolder("files"))
        val text = ClipDto(
            id = "t", seq = 1, kind = ClipKind.TEXT, text = "x", hash = "h", sizeBytes = 1,
            lastCopiedAt = Instant.parse("2026-11-02T10:00:00Z"),
        )
        try {
            store.materialize(text, { fail("no fetch"); ByteArray(0) }, codec)
            fail("expected a refusal")
        } catch (_: IllegalArgumentException) {
        }
    }
}
