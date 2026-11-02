package xyz.ficdev.privatepaste.files

import android.content.ContentProvider
import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import java.io.File
import java.io.FileNotFoundException

@RunWith(RobolectricTestRunner::class)
class DownloadsSaverTest {
    @get:Rule val tmp = TemporaryFolder()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private lateinit var media: FakeMediaStore

    @Before fun setUp() {
        media = Robolectric.setupContentProvider(FakeMediaStore::class.java, MediaStore.AUTHORITY)
        media.dir = tmp.newFolder("media")
    }

    private fun file(name: String, bytes: ByteArray) = File(tmp.newFolder(), name).apply { writeBytes(bytes) }

    @Test
    fun savesEachFileToDownloadsWithoutClobbering() {
        media.existing += "report.pdf"
        val saver = DownloadsSaver(context)

        assertTrue(saver.save(listOf(file("report.pdf", byteArrayOf(1, 2)), file("evil.exe", byteArrayOf(3)))))

        val saved = media.rows
        assertEquals(listOf("report (2).pdf", "evil.exe"), saved.map { it.name })
        assertArrayEquals(byteArrayOf(1, 2), saved[0].bytes())
        assertArrayEquals(byteArrayOf(3), saved[1].bytes())
        assertTrue("published once written", saved.all { it.values.getAsInteger(MediaStore.MediaColumns.IS_PENDING) == 0 })
        assertTrue(saved.all { it.values.getAsString(MediaStore.MediaColumns.RELATIVE_PATH) == "Download/" })
        // The MIME type comes from the (safe) name, never from the payload: an unknown extension stays a plain download.
        assertEquals("application/pdf", saved[0].values.getAsString(MediaStore.MediaColumns.MIME_TYPE))
        assertEquals("application/octet-stream", saved[1].values.getAsString(MediaStore.MediaColumns.MIME_TYPE))
    }

    @Test
    fun aFailedWriteRemovesTheEntryAndReportsFailure() {
        media.failWrites = true
        assertFalse(DownloadsSaver(context).save(listOf(file("a.txt", byteArrayOf(1)))))
        assertEquals(emptyList<String>(), media.rows.map { it.name })
    }
}

/** A tiny MediaStore: rows with values and a backing file, enough for insert, query by name, write, update and delete. */
class FakeMediaStore : ContentProvider() {
    class Row(val id: Long, val values: ContentValues, val file: File) {
        val name: String get() = values.getAsString(MediaStore.MediaColumns.DISPLAY_NAME)
        fun bytes(): ByteArray? = file.takeIf { it.isFile }?.readBytes()
    }

    lateinit var dir: File
    val rows = mutableListOf<Row>()

    /** Names already in Downloads (another app's files, say). */
    val existing = mutableListOf<String>()
    var failWrites = false
    private var nextId = 1L

    override fun onCreate() = true

    override fun insert(uri: Uri, values: ContentValues?): Uri {
        val id = nextId++
        rows += Row(id, ContentValues(values), File(dir, "row$id"))
        return ContentUris.withAppendedId(uri, id)
    }

    override fun query(uri: Uri, p: Array<out String>?, s: String?, args: Array<out String>?, o: String?): Cursor {
        val name = args?.firstOrNull()
        val cursor = MatrixCursor(arrayOf(MediaStore.MediaColumns._ID))
        if (name in existing) cursor.addRow(arrayOf(0L))
        rows.filter { it.name == name }.forEach { cursor.addRow(arrayOf(it.id)) }
        return cursor
    }

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        if (failWrites) throw FileNotFoundException("no space")
        val row = rows.first { it.id == ContentUris.parseId(uri) }
        return ParcelFileDescriptor.open(row.file, ParcelFileDescriptor.parseMode(mode))
    }

    override fun update(uri: Uri, v: ContentValues?, s: String?, a: Array<out String>?): Int {
        val row = rows.firstOrNull { it.id == ContentUris.parseId(uri) } ?: return 0
        row.values.putAll(v)
        return 1
    }

    override fun delete(uri: Uri, s: String?, a: Array<out String>?): Int =
        if (rows.removeAll { it.id == ContentUris.parseId(uri) }) 1 else 0

    override fun getType(uri: Uri): String? = null
}
