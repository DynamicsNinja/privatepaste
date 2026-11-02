using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Protocol;
using PrivatePaste.Protocol.Crypto;
using PrivatePaste.Tests.Web;

namespace PrivatePaste.Tests.Protocol;

public class VaultCryptoTests
{
    private const int Chunk = BlobCipher.ChunkSize;

    private static byte[] Range(int from, int count) => Enumerable.Range(from, count).Select(i => (byte)i).ToArray();

    private static byte[] Pattern(int size)
    {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(i % 251);
        return b;
    }

    private static async Task<byte[]> Encrypt(VaultKey key, byte[] plaintext)
    {
        using var output = new MemoryStream();
        await BlobCipher.EncryptAsync(key, new MemoryStream(plaintext), output, CancellationToken.None);
        return output.ToArray();
    }

    private static async Task<byte[]> Decrypt(VaultKey key, byte[] blob)
    {
        using var output = new MemoryStream();
        await BlobCipher.DecryptAsync(key, new MemoryStream(blob), output, CancellationToken.None);
        return output.ToArray();
    }

    [Fact]
    public void KeyId_IsStableHexOf8Bytes()
    {
        using var a = VaultKey.FromBytes(Range(0, 32));
        using var b = VaultKey.FromBytes(Range(0, 32));
        Assert.Matches("^[0-9a-f]{16}$", a.KeyId);
        Assert.Equal(a.KeyId, b.KeyId);
    }

    [Fact]
    public void VaultKey_RejectsWrongLengthAndExportsACopy()
    {
        Assert.Throws<ArgumentException>(() => VaultKey.FromBytes(new byte[31]));
        Assert.Throws<ArgumentException>(() => VaultKey.FromBytes(new byte[33]));
        using var created = VaultKey.Create();
        var exported = created.Export();
        Assert.Equal(32, exported.Length);
        using var again = VaultKey.FromBytes(exported);
        Assert.Equal(created.KeyId, again.KeyId);
        Assert.NotEqual(created.KeyId, VaultKey.Create().KeyId);
    }

    [Fact]
    public void SealedBox_RoundTripsAndRejectsWrongPurpose()
    {
        using var k = VaultKey.Create();
        var data = Encoding.UTF8.GetBytes("""{"text":"šećer"}""");
        var sealedBytes = SealedBox.Seal(k, "payload", data);

        Assert.Equal(0x01, sealedBytes[0]);
        Assert.Equal(data.Length + 29, sealedBytes.Length);
        Assert.Equal(data, SealedBox.Open(k, "payload", sealedBytes));
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(k, "thumb", sealedBytes));
        using var other = VaultKey.Create();
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(other, "payload", sealedBytes));
        for (var i = 0; i < sealedBytes.Length; i++)
        {
            var tampered = (byte[])sealedBytes.Clone();
            tampered[i] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(k, "payload", tampered));
        }
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(k, "payload", sealedBytes.AsSpan(0, 28)));
    }

    [Fact]
    public void SealedBox_OnlyAcceptsKnownPurposes()
    {
        using var k = VaultKey.Create();
        Assert.Throws<ArgumentException>(() => SealedBox.Seal(k, "blob", [1]));
        Assert.Throws<ArgumentException>(() => SealedBox.Seal(k, "handover", [1]));
        Assert.Equal([1], SealedBox.Open(k, "thumb", SealedBox.Seal(k, "thumb", [1])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    public async Task Blob_RoundTripsAtChunkBoundaries(int size)
    {
        using var k = VaultKey.Create();
        var plaintext = Pattern(size);
        var blob = await Encrypt(k, plaintext);

        var chunks = Math.Max(1, (size + Chunk - 1) / Chunk);
        Assert.Equal(9 + size + 16 * chunks, blob.Length);
        Assert.Equal(0x02, blob[0]);
        Assert.Equal(plaintext, await Decrypt(k, blob));
    }

    [Fact]
    public async Task Blob_DetectsTruncationAndReorder()
    {
        using var k = VaultKey.Create();
        var blob = await Encrypt(k, Pattern(2 * Chunk));
        const int sealedChunk = Chunk + 16;

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(k, blob[..(9 + sealedChunk)]));

        byte[] swapped = [.. blob[..9], .. blob[(9 + sealedChunk)..], .. blob[9..(9 + sealedChunk)]];
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(k, swapped));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(k, [.. blob, 0]));

        var small = await Encrypt(k, Pattern(10));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(k, [.. small, 0]));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(k, small[..^1]));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(k, small[..9]));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(k, small[..5]));
    }

    [Fact]
    public void KeyedHash_DiffersPerKeyAndMatchesForSameKey()
    {
        var contentHash = ContentHash.ForText("hello");
        using var a = VaultKey.FromBytes(Range(0, 32));
        using var a2 = VaultKey.FromBytes(Range(0, 32));
        using var b = VaultKey.FromBytes(Range(1, 32));

        Assert.Matches("^[0-9a-f]{64}$", KeyedHash.For(a, contentHash));
        Assert.Equal(KeyedHash.For(a, contentHash), KeyedHash.For(a2, contentHash));
        Assert.NotEqual(KeyedHash.For(a, contentHash), KeyedHash.For(b, contentHash));
        Assert.NotEqual(contentHash, KeyedHash.For(a, contentHash));
    }

    [Fact]
    public void Handover_RoundTripsAndChecksKeyId()
    {
        using var key = VaultKey.Create();
        var (otk, blob) = Handover.Wrap(key);
        Assert.Equal(32, otk.Length);

        using (var unwrapped = Handover.Unwrap(otk, blob, key.KeyId))
            Assert.Equal(key.KeyId, unwrapped.KeyId);
        Assert.ThrowsAny<CryptographicException>(() => Handover.Unwrap(otk, blob, "0000000000000000"));
        var wrongOtk = (byte[])otk.Clone();
        wrongOtk[0] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => Handover.Unwrap(wrongOtk, blob, key.KeyId));
        // The handover AAD differs from every vault purpose, so a vault-sealed item can't pose as a handover.
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(otk, Encoding.UTF8.GetBytes("privatepaste v1 payload"), blob));

        var id = Guid.NewGuid();
        Assert.True(Handover.TryParseQr(Handover.QrText(id, otk, key.KeyId), out var parsedId, out var parsedOtk, out var parsedKid));
        Assert.Equal(id, parsedId);
        Assert.Equal(otk, parsedOtk);
        Assert.Equal(key.KeyId, parsedKid);
        Assert.False(Handover.TryParseQr("https://x", out _, out _, out _));
    }

    [Theory]
    [InlineData("privatepaste:handover?id=00000000-0000-0000-0000-000000000001&k=AAEC&kid=0011223344556677")] // otk too short
    [InlineData("privatepaste:handover?id=nope&k=ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8&kid=0011223344556677")]
    [InlineData("privatepaste:handover?id=00000000-0000-0000-0000-000000000001&k=ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8&kid=ZZ11223344556677")]
    [InlineData("privatepaste:handover?id=00000000-0000-0000-0000-000000000001&k=ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8")]
    [InlineData("privatepaste:handover?id=00000000-0000-0000-0000-000000000001&id=00000000-0000-0000-0000-000000000002&k=ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8&kid=0011223344556677")]
    [InlineData("privatepaste:handover?id=00000000-0000-0000-0000-000000000001&k=ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=&kid=0011223344556677")] // padded
    [InlineData("privatepaste:handover?id=00000000-0000-0000-0000-000000000001&k=ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj9&kid=0011223344556677")] // stray trailing bits
    [InlineData("privatepaste:handover?id=00000000-0000-0000-0000-000000000001&k=ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6 Ozw9Pj8&kid=0011223344556677")] // whitespace
    [InlineData("")]
    public void Handover_RejectsMalformedQr(string text) =>
        Assert.False(Handover.TryParseQr(text, out _, out _, out _));

    // The cross-client vector file: the Kotlin and browser ports must reproduce every value in it byte for byte.
    // PP_WRITE_VECTORS=1 rewrites it; otherwise this test fails if the .NET code drifts from the committed file.
    [Fact]
    public async Task WriteVectors_MatchCommittedFile()
    {
        var json = await BuildVectorsJson();
        var path = Path.Combine(PlatformIconsTests.RepoRoot(), "docs", "e2e-test-vectors.json");
        if (Environment.GetEnvironmentVariable("PP_WRITE_VECTORS") == "1")
            await File.WriteAllTextAsync(path, json);
        // Git may check the file out with CRLF on Windows.
        Assert.Equal(json, (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n"));
    }

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);

    private static async Task<string> BuildVectorsJson()
    {
        var vk = Range(0, 32);
        var otk = Range(32, 32);
        var nonce = Range(0xa0, 12);
        var noncePrefix = Range(0xb0, 8);
        using var key = VaultKey.FromBytes(vk);

        const string payloadJson = """{"text":"hello"}""";
        var payloadSealed = SealedBox.Seal(key.EncKey, Encoding.UTF8.GetBytes("privatepaste v1 payload"), Encoding.UTF8.GetBytes(payloadJson), nonce);
        Assert.Equal(Encoding.UTF8.GetBytes(payloadJson), SealedBox.Open(key, "payload", payloadSealed));

        var blobPlain = Pattern(Chunk + 5);
        using var blobOut = new MemoryStream();
        await BlobCipher.EncryptAsync(key, new MemoryStream(blobPlain), blobOut, noncePrefix, CancellationToken.None);
        var blob = blobOut.ToArray();
        Assert.Equal(blobPlain, await Decrypt(key, blob));

        using var emptyOut = new MemoryStream();
        await BlobCipher.EncryptAsync(key, new MemoryStream(), emptyOut, noncePrefix, CancellationToken.None);

        var contentHash = ContentHash.ForText("hello");

        var handoverBlob = Handover.Wrap(key, otk, nonce);
        var handoverId = Guid.Parse("00000000-0000-4000-8000-000000000001");
        using (var unwrapped = Handover.Unwrap(otk, handoverBlob, key.KeyId))
            Assert.Equal(vk, unwrapped.Export());

        // A second case whose key text uses the URL-safe characters '-' and '_' (base64url sextets 62 and 63).
        var otk2 = Enumerable.Range(0, 32).Select(i => new byte[] { 0xfb, 0xff, 0xbf }[i % 3]).ToArray();
        var handover2Blob = Handover.Wrap(key, otk2, nonce);
        var handover2Id = Guid.Parse("00000000-0000-4000-8000-000000000002");
        var handover2Qr = Handover.QrText(handover2Id, otk2, key.KeyId);
        Assert.Contains("-", handover2Qr.Split("&k=")[1]);
        Assert.Contains("_", handover2Qr.Split("&k=")[1]);
        Assert.True(Handover.TryParseQr(handover2Qr, out _, out var parsed2, out _));
        Assert.Equal(otk2, parsed2);

        var thumbPlain = Range(0xc0, 20);
        var thumbSealed = SealedBox.Seal(key.EncKey, Encoding.UTF8.GetBytes("privatepaste v1 thumb"), thumbPlain, nonce);
        Assert.Equal(thumbPlain, SealedBox.Open(key, "thumb", thumbSealed));

        // Passphrase cases: fixed salt and nonce, 64 MiB / 3 / 1 as shipped.
        var salt = Range(0, 16);
        var passphrases = new[] { "correct horse battery", "Šećer na 5. katu".Normalize(NormalizationForm.FormC), "Šećer na 5. katu".Normalize(NormalizationForm.FormD) };
        Assert.NotEqual(passphrases[1], passphrases[2]);
        var argonCases = passphrases.Select(p => (p, key: PassphraseWrap.DeriveKek(p, salt, 65536, 3, 1))).ToArray();
        Assert.Equal(argonCases[1].key, argonCases[2].key);
        var wrapCases = passphrases.Take(2).Select(p => (p, wrap: PassphraseWrap.Wrap(key, p, salt, nonce))).ToArray();
        foreach (var (p, wrap) in wrapCases)
        {
            using var unwrapped = PassphraseWrap.Unwrap(wrap, p);
            Assert.Equal(vk, unwrapped.Export());
        }

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteString("about", string.Join(" ",
                "PrivatePaste E2E cross-client test vectors (protocol 2), generated by tests/PrivatePaste.Tests/Protocol/VaultCryptoTests.cs with PP_WRITE_VECTORS=1.",
                "All byte values are lowercase hex unless the field name says otherwise.",
                "vaultKey: vk (32-byte vault key) -> encKey, hashKey (HKDF-SHA256, no salt, info 'privatepaste v1 enc' / 'privatepaste v1 hash') and keyId (first 8 bytes of HKDF info 'privatepaste v1 key id').",
                "payload: small item 0x01 || nonce || ciphertext || tag under encKey with AAD 'privatepaste v1 ' + purpose; json is the UTF-8 plaintext, sealed the full output.",
                "blob: chunked format 0x02 || noncePrefix, then 1 MiB chunks; plaintext byte i = i % 251 for plaintextLength bytes; only SHA-256 digests and the length of the output are stored.",
                "emptyBlob: the full output for an empty plaintext (one final chunk).",
                "keyedHash: text -> contentHash (ContentHash.ForText) -> keyedHash = HMAC-SHA256(hashKey, UTF-8 of contentHash).",
                "fileHash: ContentHash.ForFiles: SHA-256 over UTF-8 'f:' then per file name + NUL + decimal size + NUL + sha256Hex(content) + LF; expected is lowercase hex.",
                "safeFileName: FileNames.Safe(in) = out (untrusted payload name -> safe leaf name). fileSize: FileNames.FormatSize(bytes) = text (integer half-up rounding: tenths = (bytes*10 + unit/2) / unit; one decimal only below 10 and not .0; a KB value rounding to 1024 is shown as MB). fileRowText: FileNames.RowText(files) = text. Safe: the 200-unit cut steps back one unit rather than split a surrogate pair, then trims trailing dots and spaces from the stem (empty stem -> file).",
                "handover: blob is otk-sealed small item with AAD 'privatepaste v1 handover' and plaintext vk; qrText uses id, base64url otk without padding, and keyId.",
                "handoverUrlSafe: a second handover whose otk text contains - and _. thumb: a small item with purpose thumb; plaintext is hex.",
                "argon2: passphrase (NFC-normalised, UTF-8) -> 32-byte key with the given salt and cost; the NFC and NFD spellings of the same text give the same key.",
                "passphraseWrap: the VK wrapped under a passphrase: AES-256-GCM with AAD 'privatepaste v1 vault ' + keyId under the argon2 key (salt 0..15, 64 MiB, 3, 1); wrap is the VaultWrap DTO as sent on the wire (base64)."));

            w.WriteStartObject("vaultKey");
            w.WriteString("vk", Hex(vk));
            w.WriteString("encKey", Hex(key.EncKey));
            w.WriteString("hashKey", Hex(key.HashKey));
            w.WriteString("keyId", key.KeyId);
            w.WriteEndObject();

            w.WriteStartObject("payload");
            w.WriteString("purpose", "payload");
            w.WriteString("nonce", Hex(nonce));
            w.WriteString("json", payloadJson);
            w.WriteString("sealed", Hex(payloadSealed));
            w.WriteEndObject();

            w.WriteStartObject("blob");
            w.WriteString("noncePrefix", Hex(noncePrefix));
            w.WriteNumber("chunkSize", Chunk);
            w.WriteNumber("plaintextLength", blobPlain.Length);
            w.WriteString("plaintextSha256", Hex(SHA256.HashData(blobPlain)));
            w.WriteNumber("ciphertextLength", blob.Length);
            w.WriteString("ciphertextSha256", Hex(SHA256.HashData(blob)));
            w.WriteEndObject();

            w.WriteStartObject("emptyBlob");
            w.WriteString("noncePrefix", Hex(noncePrefix));
            w.WriteString("ciphertext", Hex(emptyOut.ToArray()));
            w.WriteEndObject();

            w.WriteStartObject("keyedHash");
            w.WriteString("text", "hello");
            w.WriteString("contentHash", contentHash);
            w.WriteString("keyedHash", KeyedHash.For(key, contentHash));
            w.WriteEndObject();

            var hashFiles = new[] { ("a.txt", 5L, Hex(SHA256.HashData("hello"u8))), ("b.bin", 0L, Hex(SHA256.HashData([]))) };
            w.WriteStartObject("fileHash");
            w.WriteStartArray("files");
            foreach (var (name, size, sha) in hashFiles)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteNumber("size", size);
                w.WriteString("sha256Hex", sha);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("expected", ContentHash.ForFiles(hashFiles));
            w.WriteEndObject();

            w.WriteStartArray("safeFileName");
            foreach (var (input, output) in FileNamesTests.SafeCases)
            {
                Assert.Equal(output, FileNames.Safe(input));
                w.WriteStartObject();
                w.WriteString("in", input);
                w.WriteString("out", output);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("fileSize");
            foreach (var (bytes, text) in FileNamesTests.SizeCases)
            {
                Assert.Equal(text, FileNames.FormatSize(bytes));
                w.WriteStartObject();
                w.WriteNumber("bytes", bytes);
                w.WriteString("text", text);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("fileRowText");
            foreach (var (files, text) in FileNamesTests.RowCases)
            {
                w.WriteStartObject();
                w.WriteStartArray("files");
                foreach (var f in files)
                {
                    w.WriteStartObject();
                    w.WriteString("name", f.Name);
                    w.WriteNumber("size", f.Size);
                    w.WriteString("mime", f.Mime);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteString("text", text);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("handover");
            w.WriteString("otk", Hex(otk));
            w.WriteString("nonce", Hex(nonce));
            w.WriteString("blob", Hex(handoverBlob));
            w.WriteString("id", handoverId.ToString("D"));
            w.WriteString("qrText", Handover.QrText(handoverId, otk, key.KeyId));
            w.WriteEndObject();

            w.WriteStartObject("handoverUrlSafe");
            w.WriteString("otk", Hex(otk2));
            w.WriteString("nonce", Hex(nonce));
            w.WriteString("blob", Hex(handover2Blob));
            w.WriteString("id", handover2Id.ToString("D"));
            w.WriteString("qrText", handover2Qr);
            w.WriteEndObject();

            w.WriteStartObject("thumb");
            w.WriteString("purpose", "thumb");
            w.WriteString("nonce", Hex(nonce));
            w.WriteString("plaintext", Hex(thumbPlain));
            w.WriteString("sealed", Hex(thumbSealed));
            w.WriteEndObject();

            w.WriteStartArray("argon2");
            foreach (var (p, derived) in argonCases)
            {
                w.WriteStartObject();
                w.WriteString("passphrase", p);
                w.WriteString("salt", Hex(salt));
                w.WriteNumber("memoryKiB", 65536);
                w.WriteNumber("iterations", 3);
                w.WriteNumber("parallelism", 1);
                w.WriteString("key", Hex(derived));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("passphraseWrap");
            foreach (var (p, wrap) in wrapCases)
            {
                w.WriteStartObject();
                w.WriteString("passphrase", p);
                w.WriteString("vk", Hex(vk));
                w.WritePropertyName("wrap");
                JsonSerializer.Serialize(w, wrap, ProtocolJson.Options);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
