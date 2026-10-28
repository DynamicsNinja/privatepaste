package xyz.ficdev.privatepaste.clipboard

import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.webkit.MimeTypeMap
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import java.io.File

@RunWith(RobolectricTestRunner::class)
class ClipboardWriterTest {

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val clipboard = context.getSystemService(ClipboardManager::class.java)
    private val dir = File(context.cacheDir, "clipboard")
    private val png = byteArrayOf(0x89.toByte(), 'P'.code.toByte(), 'N'.code.toByte(), 'G'.code.toByte(), 1, 2, 3)

    /**
     * androidx FileProvider joins paths with '/', so on a Windows host it cannot map files under cacheDir. There the
     * test registers [WindowsHostFileProvider], which serves the same `content://<authority>/clipboard/<file>` layout
     * and answers getType from the extension like FileProvider. Elsewhere (Linux CI) the real FileProvider runs.
     */
    private val windowsHost = File.separatorChar == '\\'

    @Before
    fun setUp() {
        if (windowsHost) Robolectric.setupContentProvider(WindowsHostFileProvider::class.java, ClipboardWriter.AUTHORITY)
    }

    private fun writer() =
        if (windowsHost) {
            ClipboardWriter(context, ClipImageFiles(dir)) { _, f ->
                Uri.parse("content://${ClipboardWriter.AUTHORITY}/clipboard/${Uri.encode(f.name)}")
            }
        } else {
            ClipboardWriter(context, ClipImageFiles(dir))
        }

    @Test
    fun textLandsOnThePrimaryClip() {
        writer().writeText("hello from the PC")
        val clip = clipboard.primaryClip!!
        assertEquals("PrivatePaste", clip.description.label)
        assertTrue(clip.description.hasMimeType(ClipDescription.MIMETYPE_TEXT_PLAIN))
        assertEquals("hello from the PC", clip.getItemAt(0).text.toString())
        assertNotEquals(true, clip.description.extras?.containsKey("android.content.extra.IS_SENSITIVE"))
    }

    @Test
    fun imageLandsAsFileProviderUriWithPngMime() {
        writer().writeImage("clip-1", png)
        val clip = clipboard.primaryClip!!
        assertEquals("PrivatePaste", clip.description.label)
        assertTrue(clip.description.hasMimeType("image/png"))
        val uri = clip.getItemAt(0).uri
        assertEquals("content", uri.scheme)
        assertEquals("xyz.ficdev.privatepaste.files", uri.authority)
        assertTrue(uri.toString().startsWith("content://xyz.ficdev.privatepaste.files/"))
        assertEquals("image/png", context.contentResolver.getType(uri))
        val bytes = context.contentResolver.openInputStream(uri)!!.use { it.readBytes() }
        assertArrayEquals(png, bytes)
        assertNotEquals(true, clip.description.extras?.containsKey("android.content.extra.IS_SENSITIVE"))
    }

    @Test
    fun onlyTheNewest5ImageFilesAreKept() {
        val files = ClipImageFiles(dir)
        val stored = (1..7).map { files.store("clip-$it", png) }
        val left = dir.listFiles()!!.filter { it.isFile }.map { it.name }.toSet()
        assertEquals(5, left.size)
        assertEquals(stored.takeLast(5).map { it.name }.toSet(), left)
        assertFalse(stored[0].exists())
        assertFalse(stored[1].exists())

        // Storing the same clip again replaces its file instead of adding one.
        files.store("clip-7", png)
        assertEquals(5, dir.listFiles()!!.count { it.isFile })
        assertEquals(1, dir.listFiles()!!.count { it.name.endsWith("clip-7.png") })
    }

    @Test
    fun readerReturnsTextBeforeImage() {
        val reader = ClipboardReader(context)
        clipboard.clearPrimaryClip()
        assertNull(reader.read())

        writer().writeText("abc")
        assertEquals(ClipboardContent.Text("abc"), reader.read())

        writer().writeImage("clip-2", png)
        val image = reader.read() as ClipboardContent.Image
        assertEquals("image/png", image.mime)
        assertEquals("xyz.ficdev.privatepaste.files", image.uri.authority)
    }
}

/** Windows-host stand-in for FileProvider (see [ClipboardWriterTest.windowsHost]): `clipboard/<name>` under cacheDir. */
class WindowsHostFileProvider : ContentProvider() {
    override fun onCreate() = true

    private fun fileFor(uri: Uri): File {
        val segments = uri.pathSegments
        require(segments.size == 2 && segments[0] == "clipboard") { "Unknown uri $uri" }
        return File(File(context!!.cacheDir, "clipboard"), File(segments[1]).name)
    }

    override fun getType(uri: Uri): String =
        MimeTypeMap.getSingleton().getMimeTypeFromExtension(fileFor(uri).extension) ?: "application/octet-stream"

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor =
        ParcelFileDescriptor.open(fileFor(uri), ParcelFileDescriptor.MODE_READ_ONLY)

    override fun query(uri: Uri, p: Array<out String>?, s: String?, a: Array<out String>?, o: String?): Cursor? = null
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, s: String?, a: Array<out String>?) = 0
    override fun update(uri: Uri, v: ContentValues?, s: String?, a: Array<out String>?) = 0
}
