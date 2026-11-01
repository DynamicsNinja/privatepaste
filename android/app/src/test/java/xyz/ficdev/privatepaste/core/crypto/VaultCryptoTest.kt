package xyz.ficdev.privatepaste.core.crypto

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import xyz.ficdev.privatepaste.core.ContentHash
import xyz.ficdev.privatepaste.core.model.VaultWrap
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.GeneralSecurityException
import java.security.MessageDigest
import java.text.Normalizer
import java.util.UUID

class VaultCryptoTest {
    // Gradle runs unit tests with android/app as the working directory.
    private val vectors: JsonObject = Json.parseToJsonElement(
        File("../../docs/e2e-test-vectors.json").readText(Charsets.UTF_8),
    ).jsonObject

    private fun section(name: String) = vectors.getValue(name).jsonObject
    private fun JsonObject.str(name: String) = getValue(name).jsonPrimitive.content
    private fun JsonObject.bytes(name: String) = str(name).hexToBytes()

    private fun String.hexToBytes() = ByteArray(length / 2) { substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    private fun ByteArray.hex() = joinToString("") { "%02x".format(it) }
    private fun sha256(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).hex()

    private fun testKey() = VaultKey.fromBytes(section("vaultKey").bytes("vk"))

    private fun blobPlaintext(length: Int) = ByteArray(length) { (it % 251).toByte() }

    @Test
    fun vectorsMatch() {
        val key = testKey()
        val vk = section("vaultKey")
        assertEquals(vk.str("keyId"), key.keyId)
        assertEquals(vk.str("encKey"), key.useEncKey { it.hex() })
        assertEquals(vk.str("hashKey"), key.useHashKey { it.hex() })

        val payload = section("payload")
        val sealed = SealedBox.seal(key, payload.str("purpose"), payload.str("json").toByteArray(), payload.bytes("nonce"))
        assertEquals(payload.str("sealed"), sealed.hex())
        assertEquals(payload.str("json"), String(SealedBox.open(key, payload.str("purpose"), sealed)))

        val blob = section("blob")
        assertEquals(blob.str("chunkSize").toInt(), BlobCipher.CHUNK_SIZE)
        val plain = blobPlaintext(blob.str("plaintextLength").toInt())
        assertEquals(blob.str("plaintextSha256"), sha256(plain))
        val encrypted = ByteArrayOutputStream()
        BlobCipher.encrypt(key, ByteArrayInputStream(plain), encrypted, blob.bytes("noncePrefix"))
        assertEquals(blob.str("ciphertextLength").toInt(), encrypted.size())
        assertEquals(blob.str("ciphertextSha256"), sha256(encrypted.toByteArray()))
        val decrypted = ByteArrayOutputStream()
        BlobCipher.decrypt(key, ByteArrayInputStream(encrypted.toByteArray()), decrypted)
        assertArrayEquals(plain, decrypted.toByteArray())

        val empty = section("emptyBlob")
        val emptyOut = ByteArrayOutputStream()
        BlobCipher.encrypt(key, ByteArrayInputStream(ByteArray(0)), emptyOut, empty.bytes("noncePrefix"))
        assertEquals(empty.str("ciphertext"), emptyOut.toByteArray().hex())

        val keyed = section("keyedHash")
        assertEquals(keyed.str("contentHash"), ContentHash.text(keyed.str("text")))
        assertEquals(keyed.str("keyedHash"), KeyedHash.forHash(key, keyed.str("contentHash")))

        val handover = section("handover")
        assertEquals(handover.str("blob"), Handover.wrap(key, handover.bytes("otk"), handover.bytes("nonce")).hex())
        val opened = Handover.unwrap(handover.bytes("otk"), handover.bytes("blob"), vk.str("keyId"))
        assertEquals(vk.str("keyId"), opened.keyId)
        assertEquals(
            handover.str("qrText"),
            Handover.qrText(UUID.fromString(handover.str("id")), handover.bytes("otk"), vk.str("keyId")),
        )
        val qr = Handover.parseQr(handover.str("qrText"))
        assertNotNull(qr)
        assertEquals(UUID.fromString(handover.str("id")), qr!!.id)
        assertArrayEquals(handover.bytes("otk"), qr.otk)
        assertEquals(vk.str("keyId"), qr.keyId)

        // An otk whose base64url uses '-' and '_' (and whose last character carries spare bits).
        val urlSafe = section("handoverUrlSafe")
        assertEquals(urlSafe.str("blob"), Handover.wrap(key, urlSafe.bytes("otk"), urlSafe.bytes("nonce")).hex())
        assertEquals(
            urlSafe.str("qrText"),
            Handover.qrText(UUID.fromString(urlSafe.str("id")), urlSafe.bytes("otk"), vk.str("keyId")),
        )
        val urlSafeQr = Handover.parseQr(urlSafe.str("qrText"))
        assertNotNull(urlSafeQr)
        assertEquals(UUID.fromString(urlSafe.str("id")), urlSafeQr!!.id)
        assertArrayEquals(urlSafe.bytes("otk"), urlSafeQr.otk)
        assertEquals(vk.str("keyId"), urlSafeQr.keyId)
        assertEquals(vk.str("keyId"), Handover.unwrap(urlSafeQr.otk, urlSafe.bytes("blob"), urlSafeQr.keyId).keyId)

        val thumb = section("thumb")
        val sealedThumb = SealedBox.seal(key, thumb.str("purpose"), thumb.bytes("plaintext"), thumb.bytes("nonce"))
        assertEquals(thumb.str("sealed"), sealedThumb.hex())
        assertArrayEquals(thumb.bytes("plaintext"), SealedBox.open(key, thumb.str("purpose"), thumb.bytes("sealed")))
    }

    // Entry shapes: argon2 {passphrase, salt (hex), memoryKiB, iterations, parallelism, key (hex)} and
    // passphraseWrap {passphrase, vk (hex), wrap (a VaultWrap as on the wire)}.
    @Test
    fun argon2VectorsMatch() {
        val argon2 = vectors.getValue("argon2") as JsonArray
        val wraps = vectors.getValue("passphraseWrap") as JsonArray
        assertTrue("argon2 vectors are present", argon2.isNotEmpty())
        assertTrue("passphraseWrap vectors are present", wraps.isNotEmpty())
        for (case in argon2) {
            val c = case.jsonObject
            val kek = PassphraseWrap.deriveKek(
                c.str("passphrase"), c.bytes("salt"),
                c.str("memoryKiB").toInt(), c.str("iterations").toInt(), c.str("parallelism").toInt(),
            )
            assertEquals(c.str("key"), kek.hex())
        }
        for (case in wraps) {
            val c = case.jsonObject
            val wrap = Json.decodeFromJsonElement(VaultWrap.serializer(), c.getValue("wrap"))
            val key = PassphraseWrap.unwrap(wrap, c.str("passphrase"))
            assertEquals(wrap.keyId, key.keyId)
            assertEquals(c.str("vk"), key.export().hex())
        }
    }

    @Test
    fun sealedBoxRoundTripsAndRejectsTampering() {
        val key = VaultKey.create()
        val box = SealedBox.seal(key, "payload", "hello".toByteArray())
        assertArrayEquals("hello".toByteArray(), SealedBox.open(key, "payload", box))
        assertThrows(GeneralSecurityException::class.java) { SealedBox.open(key, "thumb", box) }
        assertThrows(GeneralSecurityException::class.java) { SealedBox.open(VaultKey.create(), "payload", box) }
        val tampered = box.clone().also { it[20] = (it[20].toInt() xor 1).toByte() }
        assertThrows(GeneralSecurityException::class.java) { SealedBox.open(key, "payload", tampered) }
        assertThrows(GeneralSecurityException::class.java) { SealedBox.open(key, "payload", byteArrayOf(1, 2, 3)) }
        assertThrows(IllegalArgumentException::class.java) { SealedBox.seal(key, "blob", ByteArray(1)) }
    }

    @Test
    fun blobRoundTripsAtChunkBoundaries() {
        val key = VaultKey.create()
        val sizes = listOf(0, 1, BlobCipher.CHUNK_SIZE - 1, BlobCipher.CHUNK_SIZE, BlobCipher.CHUNK_SIZE + 1, 2 * BlobCipher.CHUNK_SIZE)
        for (length in sizes) {
            val plain = blobPlaintext(length)
            val enc = ByteArrayOutputStream().also { BlobCipher.encrypt(key, ByteArrayInputStream(plain), it) }
            val dec = ByteArrayOutputStream().also { BlobCipher.decrypt(key, ByteArrayInputStream(enc.toByteArray()), it) }
            assertArrayEquals("length $length", plain, dec.toByteArray())
        }
    }

    @Test
    fun blobRejectsTamperingTruncationReorderAndTrailingData() {
        val key = VaultKey.create()
        val plain = blobPlaintext(2 * BlobCipher.CHUNK_SIZE + 10)
        val enc = ByteArrayOutputStream().also { BlobCipher.encrypt(key, ByteArrayInputStream(plain), it) }.toByteArray()
        fun decrypt(bytes: ByteArray) = BlobCipher.decrypt(key, ByteArrayInputStream(bytes), ByteArrayOutputStream())

        decrypt(enc)
        assertThrows(GeneralSecurityException::class.java) { decrypt(enc.clone().also { it[100] = (it[100].toInt() xor 1).toByte() }) }
        val chunk = BlobCipher.CHUNK_SIZE + 16
        // Dropping the last chunk leaves a non-final chunk at the end.
        assertThrows(GeneralSecurityException::class.java) { decrypt(enc.copyOf(9 + 2 * chunk)) }
        assertThrows(GeneralSecurityException::class.java) { decrypt(enc.copyOf(enc.size - 1)) }
        assertThrows(GeneralSecurityException::class.java) { decrypt(enc + ByteArray(5)) }
        val swapped = enc.clone()
        System.arraycopy(enc, 9 + chunk, swapped, 9, chunk)
        System.arraycopy(enc, 9, swapped, 9 + chunk, chunk)
        assertThrows(GeneralSecurityException::class.java) { decrypt(swapped) }
        assertThrows(GeneralSecurityException::class.java) { decrypt(byteArrayOf(1, 2, 3)) }
        assertThrows(GeneralSecurityException::class.java) { decrypt(ByteArray(0)) }
    }

    @Test
    fun handoverRoundTripsAndChecksKeyId() {
        val key = VaultKey.create()
        val (otk, blob) = Handover.wrap(key)
        assertEquals(key.keyId, Handover.unwrap(otk, blob, key.keyId).keyId)
        assertThrows(GeneralSecurityException::class.java) { Handover.unwrap(otk, blob, "0000000000000000") }
        assertThrows(GeneralSecurityException::class.java) { Handover.unwrap(ByteArray(32), blob, key.keyId) }
        assertThrows(GeneralSecurityException::class.java) { Handover.unwrap(ByteArray(5), blob, key.keyId) }

        val id = UUID.randomUUID()
        val qr = Handover.parseQr(Handover.qrText(id, otk, key.keyId))
        assertNotNull(qr)
        assertEquals(id, qr!!.id)
        assertArrayEquals(otk, qr.otk)
        assertEquals(key.keyId, qr.keyId)
        assertNull(Handover.parseQr("https://x"))
    }

    @Test
    fun parseQrRejectsMalformedCodes() {
        val good = "privatepaste:handover?id=00000000-0000-4000-8000-000000000001&k=ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8&kid=13dee80fba1d5400"
        assertNotNull(Handover.parseQr(good))
        assertNull(Handover.parseQr("$good&id=00000000-0000-4000-8000-000000000002"))
        assertNull(Handover.parseQr(good.replace("kid=13dee80fba1d5400", "kid=XYZ")))
        assertNull(Handover.parseQr(good.replace("id=00000000-0000-4000-8000-000000000001", "id=nope")))
        assertNull(Handover.parseQr(good.replace("&k=ICEi", "&k=ICEiX")))
        assertNull(Handover.parseQr(good.replace("&kid=13dee80fba1d5400", "")))
        assertNotNull(Handover.parseQr("$good&future=1"))
        // Only the canonical encoding: "...Pj9" decodes to the same bytes as "...Pj8" but isn't what qrText writes.
        assertNull(Handover.parseQr(good.replace("Pj8&", "Pj9&")))
    }

    @Test
    fun keyExportRoundTrips() {
        val key = VaultKey.create()
        assertEquals(key.keyId, VaultKey.fromBytes(key.export()).keyId)
        assertThrows(IllegalArgumentException::class.java) { VaultKey.fromBytes(ByteArray(31)) }
    }

    @Test
    fun passphraseWrapRoundTrips() {
        val key = VaultKey.create()
        val wrap = PassphraseWrap.wrap(key, "correct horse battery")
        val b64 = java.util.Base64.getDecoder()
        assertEquals(key.keyId, wrap.keyId)
        assertEquals("argon2id", wrap.kdf.alg)
        assertEquals(65536, wrap.kdf.memoryKiB)
        assertEquals(3, wrap.kdf.iterations)
        assertEquals(1, wrap.kdf.parallelism)
        assertEquals(16, b64.decode(wrap.kdf.salt).size)
        assertEquals(12, b64.decode(wrap.nonce).size)
        assertEquals(48, b64.decode(wrap.wrappedKey).size)
        assertArrayEquals(key.export(), PassphraseWrap.unwrap(wrap, "correct horse battery").export())
    }

    @Test
    fun wrongPassphraseThrows() {
        val wrap = PassphraseWrap.wrap(VaultKey.create(), "correct horse battery")
        assertThrows(WrongPassphraseException::class.java) { PassphraseWrap.unwrap(wrap, "correct horse batterz") }
    }

    @Test
    fun nfdAndNfcUnlockTheSameWrap() {
        val key = VaultKey.create()
        val phrase = "Šećer na 5. katu"
        val nfc = Normalizer.normalize(phrase, Normalizer.Form.NFC)
        val nfd = Normalizer.normalize(phrase, Normalizer.Form.NFD)
        assertNotEquals(nfc, nfd)
        assertArrayEquals(key.export(), PassphraseWrap.unwrap(PassphraseWrap.wrap(key, nfc), nfd).export())
        assertArrayEquals(key.export(), PassphraseWrap.unwrap(PassphraseWrap.wrap(key, nfd), nfc).export())
    }

    @Test
    fun problemRejectsShortAndCommon() {
        PassphraseWrap.useCommonList(ByteArrayInputStream("# comment\nPassword1234\n\nletmeinletmein\n".toByteArray()))
        try {
            assertEquals(PassphraseWrap.RULE_MESSAGE, PassphraseWrap.problem("short"))
            assertEquals(PassphraseWrap.COMMON_MESSAGE, PassphraseWrap.problem("password1234"))
            assertEquals(PassphraseWrap.COMMON_MESSAGE, PassphraseWrap.problem("letmeinletmein"))
            assertNull(PassphraseWrap.problem("# comment      "))
            assertNull(PassphraseWrap.problem("  letmeinletmein  "))
            assertNull(PassphraseWrap.problem("my cat eats lemons daily"))
        } finally {
            PassphraseWrap.useCommonList(ByteArrayInputStream(ByteArray(0)))
        }
    }

    // The SHA-256 of the shared list (src/PrivatePaste.Server/wwwroot/js/common-passphrases.txt); update it with the list.
    @Test
    fun bundledCommonListIsTheSharedList() {
        val bundled = File("src/main/assets/common-passphrases.txt")
        assertEquals("8ce67ae3206188e600ba31179311f8a937d359cdcc963dae997d42e2ca9cb75f", sha256(bundled.readBytes()))
        val web = File("../../src/PrivatePaste.Server/wwwroot/js/common-passphrases.txt")
        if (web.exists()) assertEquals(sha256(web.readBytes()), sha256(bundled.readBytes()))
    }

    @Test
    fun bundledCommonListRejectsAKnownEntry() {
        val bundled = File("src/main/assets/common-passphrases.txt")
        val entry = bundled.readLines().first { it.isNotEmpty() && !it.startsWith("#") }
        PassphraseWrap.useCommonList(bundled.inputStream())
        try {
            assertEquals(PassphraseWrap.COMMON_MESSAGE, PassphraseWrap.problem(entry.uppercase()))
        } finally {
            PassphraseWrap.useCommonList(ByteArrayInputStream(ByteArray(0)))
        }
    }

    @Test
    fun unwrapRejectsHostileWraps() {
        val wrap = PassphraseWrap.wrap(VaultKey.create(), "correct horse battery")
        val phrase = "correct horse battery"
        fun rejects(bad: VaultWrap) {
            assertThrows(GeneralSecurityException::class.java) { PassphraseWrap.unwrap(bad, phrase) }
        }
        rejects(wrap.copy(nonce = "not base64!"))
        rejects(wrap.copy(nonce = ""))
        rejects(wrap.copy(wrappedKey = "AAAA"))
        rejects(wrap.copy(keyId = "XYZ"))
        rejects(wrap.copy(kdf = wrap.kdf.copy(alg = "pbkdf2")))
        rejects(wrap.copy(kdf = wrap.kdf.copy(salt = "AAAA")))
        rejects(wrap.copy(kdf = wrap.kdf.copy(memoryKiB = 1_048_576)))
        rejects(wrap.copy(kdf = wrap.kdf.copy(memoryKiB = 1024)))
        rejects(wrap.copy(kdf = wrap.kdf.copy(iterations = 1_000_000)))
        rejects(wrap.copy(kdf = wrap.kdf.copy(parallelism = 0)))
        // A flipped nonce byte is a well-formed wrap that simply doesn't open.
        val nonce = java.util.Base64.getDecoder().decode(wrap.nonce).also { it[0] = (it[0].toInt() xor 1).toByte() }
        assertThrows(WrongPassphraseException::class.java) {
            PassphraseWrap.unwrap(wrap.copy(nonce = java.util.Base64.getEncoder().encodeToString(nonce)), phrase)
        }
    }

    @Test
    fun closedKeyIsWipedAndUnusable() {
        val key = VaultKey.create()
        val copy = key.useEncKey { it.clone() }
        key.close()
        assertTrue(key.isWiped())
        assertFalse("a copy taken before the close is not wiped", copy.all { it == 0.toByte() })
        assertThrows(VaultKeyClosedException::class.java) { key.export() }
        assertThrows(VaultKeyClosedException::class.java) { SealedBox.seal(key, "payload", ByteArray(1)) }
        assertThrows(VaultKeyClosedException::class.java) { SealedBox.open(key, "payload", ByteArray(40)) }
        assertThrows(VaultKeyClosedException::class.java) { KeyedHash.forHash(key, "x") }
        assertThrows(VaultKeyClosedException::class.java) {
            BlobCipher.encrypt(key, ByteArrayInputStream(ByteArray(1)), ByteArrayOutputStream())
        }
    }

    /** A close racing seals and opens never lets crypto run on the wiped (all-zero) key. */
    @Test
    fun closeRacingSealsNeverUsesZeroedBytes() {
        val zeroKey = VaultKey.fromBytes(ByteArray(32))   // what a wiped vk would derive to
        repeat(200) { round ->
            val key = VaultKey.create()
            val real = VaultKey.fromBytes(key.export())
            val sealed = java.util.concurrent.ConcurrentLinkedQueue<ByteArray>()
            val opened = java.util.concurrent.atomic.AtomicInteger()
            val good = SealedBox.seal(real, "payload", "x".toByteArray())
            val start = java.util.concurrent.CountDownLatch(1)
            val workers = List(4) {
                Thread {
                    start.await()
                    repeat(50) {
                        try {
                            sealed += SealedBox.seal(key, "payload", "secret $round".toByteArray())
                            SealedBox.open(key, "payload", good)   // never fails as tampered: it opens, or the key is closed
                            opened.incrementAndGet()
                        } catch (_: VaultKeyClosedException) {
                        }
                    }
                }.apply { start() }
            }
            start.countDown()
            key.close()
            workers.forEach { it.join() }
            for (box in sealed) {
                // Everything sealed opens under the real key, never under the wiped one.
                assertArrayEquals("secret $round".toByteArray(), SealedBox.open(real, "payload", box))
                assertThrows(GeneralSecurityException::class.java) { SealedBox.open(zeroKey, "payload", box) }
                // A wiped encKey (zeros, not derived from zeros) can't open it either.
                assertThrows(GeneralSecurityException::class.java) {
                    SealedBox.open(ByteArray(32), "privatepaste v1 payload".toByteArray(), box)
                }
            }
        }
    }
}
