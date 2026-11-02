package xyz.ficdev.privatepaste.service

import android.Manifest
import android.content.ClipboardManager
import android.content.Intent
import android.net.Uri
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import xyz.ficdev.privatepaste.clipboard.ClipboardWriter
import java.io.File

/** Copy, Share and the file store's lifetime, through the real app graph. */
@RunWith(RobolectricTestRunner::class)
@Config(application = TestPrivatePasteApp::class)
class FileClipGraphTest {
    @get:Rule val tmp = TemporaryFolder()

    private val app = ApplicationProvider.getApplicationContext<TestPrivatePasteApp>()
    private val clipboard = app.getSystemService(ClipboardManager::class.java)
    private lateinit var server: FakeServer
    private val filesDir get() = File(app.cacheDir, "files")

    @Before fun setUp() {
        TestPrivatePasteApp.setUpHostProvider()
        shadowOf(app).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
        server = FakeServer()
        app.signInTo(server)
    }

    @After fun tearDown() {
        app.shutDownForTest()
        server.shutdown()
    }

    @Test
    fun copyPutsEveryFileOnTheClipboardAndWorksOfflineOnceDownloaded() {
        app.graph.cache.upsert(server.fileClip("f1", tmp.root, "a.txt" to byteArrayOf(1), "a.txt" to byteArrayOf(2)), fromSync = true)

        assertTrue(runBlocking { app.graph.copyClip("f1") })

        val clip = clipboard.primaryClip!!
        assertEquals(2, clip.itemCount)
        val uris = (0 until 2).map { clip.getItemAt(it).uri }
        assertEquals(listOf("a.txt", "a (2).txt"), uris.map { it.lastPathSegment })
        assertArrayEquals(byteArrayOf(2), app.contentResolver.openInputStream(uris[1])!!.use { it.readBytes() })

        server.shutdown()   // offline: the downloaded files are used again
        clipboard.setPrimaryClip(android.content.ClipData.newPlainText("x", "before"))
        assertTrue(runBlocking { app.graph.copyClip("f1") })
        assertEquals(2, clipboard.primaryClip!!.itemCount)
    }

    @Test
    fun copyOfflineBeforeDownloadFails() {
        app.graph.cache.upsert(server.fileClip("f1", tmp.root, "a.txt" to byteArrayOf(1)), fromSync = true)
        server.shutdown()
        assertFalse(runBlocking { app.graph.copyClip("f1") })
    }

    @Test
    fun shareBuildsAChooserWithReadableUris() {
        app.graph.cache.upsert(server.fileClip("one", tmp.root, "report.pdf" to byteArrayOf(1)), fromSync = true)
        app.graph.cache.upsert(server.fileClip("two", tmp.root, "a.txt" to byteArrayOf(1), "b.png" to byteArrayOf(2)), fromSync = true)

        val single = runBlocking { app.graph.shareClipFiles("one") }!!
        assertEquals(Intent.ACTION_CHOOSER, single.action)
        val send = chosen(single)
        assertEquals(Intent.ACTION_SEND, send.action)
        assertEquals("application/pdf", send.type)
        assertTrue(send.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
        assertEquals(ClipboardWriter.AUTHORITY, stream(send).authority)

        val multiple = chosen(runBlocking { app.graph.shareClipFiles("two") }!!)
        assertEquals(Intent.ACTION_SEND_MULTIPLE, multiple.action)
        assertTrue(multiple.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
        assertEquals(listOf("a.txt", "b.png"), streams(multiple).map { it.lastPathSegment })

        assertNull("not a file clip", runBlocking { app.graph.shareClipFiles("nope") })
    }

    @Test
    fun filesArePrunedWithTheCacheAndClearedOnSignOut() {
        app.graph.cache.upsert(server.fileClip("keep", tmp.root, "k.txt" to byteArrayOf(1)), fromSync = true)
        val gone = server.fileClip("gone", tmp.root, "g.txt" to byteArrayOf(2))
        app.graph.cache.upsert(gone, fromSync = true)
        assertTrue(runBlocking { app.graph.copyClip("keep") })
        assertTrue(runBlocking { app.graph.copyClip("gone") })
        assertTrue(File(filesDir, "gone").isDirectory)

        app.graph.cache.remove("gone", seq = 99)
        awaitUntil("the removed clip's files are pruned") { !File(filesDir, "gone").exists() }
        assertTrue(File(filesDir, "keep/k.txt").isFile)

        runBlocking { app.uiDeps.signOut() }
        assertTrue("plaintext copies are gone with the session", filesDir.listFiles().orEmpty().isEmpty())
    }

    private fun chosen(chooser: Intent): Intent {
        @Suppress("DEPRECATION")
        return chooser.getParcelableExtra(Intent.EXTRA_INTENT)!!
    }

    @Suppress("DEPRECATION")
    private fun stream(send: Intent): Uri = send.getParcelableExtra(Intent.EXTRA_STREAM)!!

    @Suppress("DEPRECATION")
    private fun streams(send: Intent): List<Uri> = send.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)!!
}
