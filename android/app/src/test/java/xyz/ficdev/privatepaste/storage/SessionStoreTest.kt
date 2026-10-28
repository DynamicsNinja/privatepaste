package xyz.ficdev.privatepaste.storage

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import java.io.File

@RunWith(RobolectricTestRunner::class)
class SessionStoreTest {

    @get:Rule val tmp = TemporaryFolder()

    private val keys = InMemoryKeyProvider()
    private val session = StoredSession("https://paste.example", "dev-1", "Pixel 9", "tok-abc")

    private fun store() = SessionStore(CryptoFile(File(tmp.root, "session.bin"), keys, KeyAliases.SESSION))

    @Test
    fun savesAndLoads() {
        assertNull(store().load())
        store().save(session)
        assertEquals(session, store().load())
    }

    @Test
    fun clearSignsOut() {
        store().save(session)
        store().clear()
        assertNull(store().load())
    }

    @Test
    fun unreadableSessionLoadsAsSignedOut() {
        store().save(session)
        keys.forget(KeyAliases.SESSION)   // Keystore key gone or invalidated
        assertNull(store().load())

        File(tmp.root, "session.bin").writeBytes(ByteArray(64) { it.toByte() })   // corrupt file
        assertNull(store().load())

        // Decrypts fine but is not a session.
        CryptoFile(File(tmp.root, "session.bin"), keys, KeyAliases.SESSION).write("{\"nope\":1}".toByteArray())
        assertNull(store().load())

        // Signing in again works afterwards.
        store().save(session)
        assertEquals(session, store().load())
    }
}
