package xyz.ficdev.privatepaste.storage

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import java.io.File

@RunWith(RobolectricTestRunner::class)
class ThumbnailStoreTest {

    @get:Rule val tmp = TemporaryFolder()

    private val keys = InMemoryKeyProvider()
    private val dir get() = File(tmp.root, "thumbs")
    private val png = "THUMB-PLAINTEXT".toByteArray()

    @Test
    fun putGetPruneClear() {
        val store = ThumbnailStore(dir, keys)
        assertNull(store.get("aa"))
        store.put("aa", png)
        store.put("bb", png)
        assertArrayEquals(png, ThumbnailStore(dir, keys).get("aa"))
        assertFalse(String(dir.listFiles()!!.first().readBytes(), Charsets.ISO_8859_1).contains("THUMB-PLAINTEXT"))

        store.prune(setOf("bb"))
        assertNull(store.get("aa"))
        assertArrayEquals(png, store.get("bb"))

        store.clear()
        assertNull(store.get("bb"))
        assertTrue(dir.listFiles().orEmpty().isEmpty())
    }

    @Test
    fun unsafeHashesAreRejected() {
        val store = ThumbnailStore(dir, keys)
        store.put("../escape", png)
        assertNull(store.get("../escape"))
        assertFalse(File(tmp.root, "escape.bin").exists())
    }
}
