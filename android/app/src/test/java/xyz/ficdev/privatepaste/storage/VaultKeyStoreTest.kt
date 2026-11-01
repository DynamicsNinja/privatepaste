package xyz.ficdev.privatepaste.storage

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.core.crypto.VaultKey
import java.io.File

@RunWith(RobolectricTestRunner::class)
class VaultKeyStoreTest {

    @get:Rule val tmp = TemporaryFolder()

    private val keys = InMemoryKeyProvider()
    private fun file() = CryptoFile(File(tmp.root, "vault.bin"), keys, KeyAliases.VAULT)

    @Test
    fun savesLoadsAndDeletes() {
        val store = VaultKeyStore(file())
        assertNull(store.load())

        val key = VaultKey.create()
        store.save(key)
        val loaded = VaultKeyStore(file()).load()!!
        assertEquals(key.keyId, loaded.keyId)
        assertArrayEquals(key.export(), loaded.export())

        store.delete()
        assertNull(store.load())
    }

    @Test
    fun theKeyIsNotStoredInTheClear() {
        val key = VaultKey.create()
        VaultKeyStore(file()).save(key)
        val raw = key.export()
        val onDisk = File(tmp.root, "vault.bin").readBytes()
        assertFalse(onDisk.toList().windowed(raw.size).any { it == raw.toList() })
    }

    @Test
    fun aWrongLengthOrUnreadableFileIsNoKey() {
        file().write(ByteArray(5))
        assertNull(VaultKeyStore(file()).load())
        File(tmp.root, "vault.bin").writeBytes(ByteArray(64))
        assertNull(VaultKeyStore(file()).load())
    }
}
