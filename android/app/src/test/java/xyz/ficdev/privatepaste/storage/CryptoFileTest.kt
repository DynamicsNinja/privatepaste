package xyz.ficdev.privatepaste.storage

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import java.io.File
import javax.crypto.SecretKey

@RunWith(RobolectricTestRunner::class)
class CryptoFileTest {

    @get:Rule val tmp = TemporaryFolder()

    private val keys = InMemoryKeyProvider()
    private val plaintext = "secret token 0123456789 plaintext marker".toByteArray()

    private fun cryptoFile(name: String = "data.bin", provider: KeyProvider = keys) =
        CryptoFile(File(tmp.root, name), provider, "alias")

    @Test
    fun roundTrips() {
        val f = cryptoFile()
        f.write(plaintext)
        assertArrayEquals(plaintext, f.read())

        f.write("second".toByteArray())
        assertArrayEquals("second".toByteArray(), cryptoFile().read())
        assertFalse("no temp file left behind", File(tmp.root, "data.bin.tmp").exists())
    }

    @Test
    fun missingFileReadsNull() {
        assertNull(cryptoFile().read())
    }

    @Test
    fun fileOnDiskDoesNotContainPlaintext() {
        cryptoFile().write(plaintext)
        val onDisk = File(tmp.root, "data.bin").readBytes()
        assertFalse(String(onDisk, Charsets.ISO_8859_1).contains("plaintext marker"))
        // 12-byte IV + ciphertext + 16-byte tag
        assertTrue(onDisk.size == 12 + plaintext.size + 16)
    }

    @Test
    fun tamperedByteReadsNull() {
        cryptoFile().write(plaintext)
        val file = File(tmp.root, "data.bin")
        for (index in listOf(0, 12, file.length().toInt() - 1)) {
            cryptoFile().write(plaintext)
            val bytes = file.readBytes()
            bytes[index] = (bytes[index].toInt() xor 0x01).toByte()
            file.writeBytes(bytes)
            assertNull("tampered byte $index", cryptoFile().read())
        }
        file.writeBytes(byteArrayOf(1, 2, 3))
        assertNull("truncated", cryptoFile().read())
    }

    @Test
    fun missingKeyReadsNull() {
        cryptoFile().write(plaintext)
        keys.forget("alias")
        assertNull(cryptoFile().read())
    }

    @Test
    fun throwingKeyProviderReadsNull() {
        cryptoFile().write(plaintext)
        val broken = object : KeyProvider {
            override fun key(alias: String): SecretKey = throw java.security.UnrecoverableKeyException("gone")
        }
        assertNull(cryptoFile(provider = broken).read())
    }

    @Test
    fun failedKeyOnWriteIsRecreated() {
        var deleted = 0
        val flaky = object : KeyProvider {
            override fun key(alias: String): SecretKey =
                if (deleted == 0) throw android.security.keystore.KeyPermanentlyInvalidatedException("invalid")
                else keys.key(alias)

            override fun deleteKey(alias: String) {
                deleted++
                keys.deleteKey(alias)
            }
        }
        val f = cryptoFile(provider = flaky)
        f.write(plaintext)
        assertTrue(deleted == 1)
        assertArrayEquals(plaintext, f.read())
    }

    @Test
    fun transientKeystoreErrorKeepsTheKey() {
        cryptoFile().write(plaintext)   // an existing file under the same alias must stay readable
        var calls = 0
        var deleted = 0
        val transient = object : KeyProvider {
            override fun key(alias: String): SecretKey =
                if (calls++ == 0) throw java.security.ProviderException("Keystore busy") else keys.key(alias)

            override fun deleteKey(alias: String) {
                deleted++
                keys.deleteKey(alias)
            }
        }
        cryptoFile("other.bin", transient).write("second".toByteArray())
        assertEquals(0, deleted)
        assertArrayEquals("second".toByteArray(), cryptoFile("other.bin").read())
        assertArrayEquals(plaintext, cryptoFile().read())
    }

    @Test
    fun persistentTransientErrorThrowsIOExceptionWithoutDeletingTheKey() {
        var deleted = 0
        val broken = object : KeyProvider {
            override fun key(alias: String): SecretKey = throw java.security.ProviderException("Keystore down")
            override fun deleteKey(alias: String) {
                deleted++
            }
        }
        try {
            cryptoFile(provider = broken).write(plaintext)
            fail("expected IOException")
        } catch (_: java.io.IOException) {
        }
        assertEquals(0, deleted)
    }

    @Test
    fun invalidKeyInTheCauseChainIsRecreated() {
        var deleted = 0
        val wrapped = object : KeyProvider {
            override fun key(alias: String): SecretKey =
                if (deleted == 0) throw IllegalStateException(java.security.UnrecoverableKeyException("broken"))
                else keys.key(alias)

            override fun deleteKey(alias: String) {
                deleted++
                keys.deleteKey(alias)
            }
        }
        cryptoFile(provider = wrapped).write(plaintext)
        assertEquals(1, deleted)
        assertArrayEquals(plaintext, cryptoFile().read())
    }

    @Test
    fun deleteRemovesFile() {
        val f = cryptoFile()
        f.write(plaintext)
        f.delete()
        assertFalse(File(tmp.root, "data.bin").exists())
        assertNull(f.read())
    }
}
