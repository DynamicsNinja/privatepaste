package xyz.ficdev.privatepaste.clipboard

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class ClipImageFilesTest {
    @get:Rule val tmp = TemporaryFolder()

    @Test
    fun findReturnsTheStoredFileUntilItRotatesOut() {
        val files = ClipImageFiles(tmp.newFolder("clipboard"))
        assertNull(files.find("a"))

        files.store("a", byteArrayOf(1))
        files.store("a", byteArrayOf(2))   // replaced
        files.store("b/../x", byteArrayOf(3))
        assertArrayEquals(byteArrayOf(2), files.find("a")!!.readBytes())
        assertArrayEquals(byteArrayOf(3), files.find("b/../x")!!.readBytes())
        assertNull(files.find("missing"))

        repeat(5) { files.store("n$it", byteArrayOf(it.toByte())) }
        assertNull("only the newest 5 are kept", files.find("a"))
    }

    @Test
    fun clearDeletesEveryFile() {
        val dir = tmp.newFolder("clipboard")
        val files = ClipImageFiles(dir)
        files.store("a", byteArrayOf(1))
        files.store("b", byteArrayOf(2))
        files.clear()
        assertNull(files.find("a"))
        assertNull(files.find("b"))
        assertEquals(0, dir.listFiles().orEmpty().size)
        files.clear()   // also when already empty
    }
}
