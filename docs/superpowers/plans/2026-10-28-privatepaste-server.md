# PrivatePaste Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the self-hosted PrivatePaste server as one Docker container. It stores encrypted clipboard history, serves a REST + WebSocket API to device clients, and provides a minimal web UI.

**Architecture:** ASP.NET Core (.NET 10) minimal APIs plus Razor Pages. Clip metadata, text and the FTS5 search index live in an encrypted SQLite database. Images are AES-256-GCM-encrypted files. Devices sign in with the owner password, get a bearer token, and receive pushes over a WebSocket. A shared `PrivatePaste.Protocol` library holds DTOs and the content-hash algorithm so the desktop client (next plan) uses exactly the same definitions.

**Tech Stack:** .NET 10 SDK, ASP.NET Core, Microsoft.Data.Sqlite.Core + SQLite3 Multiple Ciphers (`SQLite3MC.PCLRaw.bundle`), SkiaSharp 3.x (+ `SkiaSharp.NativeAssets.Linux.NoDependencies`), `Konscious.Security.Cryptography.Argon2`, xUnit + `Microsoft.AspNetCore.Mvc.Testing`, Docker.

**Spec:** `docs/superpowers/specs/2026-10-28-privatepaste-design.md`

**Scope:** This is plan 1 of 3. The desktop app (Windows first, then macOS/Linux) and the Android app get their own plans. The Android background-clipboard spike (spec §7.1) is a throwaway and doesn't go through this plan.

**Deviation from spec (Claude's call, recorded):** The spec names `SQLitePCLRaw.bundle_e_sqlcipher`. That bundle is no longer maintained, so this plan uses SQLite3 Multiple Ciphers (`SQLite3MC.PCLRaw.bundle`, default cipher ChaCha20-Poly1305). It is a maintained drop-in that encrypts the whole database, including FTS5. Same intent: the full DB file is encrypted with `PP_DB_KEY`.

## Global Constraints

- Target framework `net10.0` for all projects; nullable enabled; warnings as errors.
- One container, port **8080**, data volume **`/data`** (overridable with `PP_DATA_DIR` for tests/dev).
- Env vars: `PP_DB_KEY` (required, ≥ 16 chars), `PP_PASSWORD` (required), `PP_MAX_IMAGE_MB` (default **20**), `PP_MAX_TEXT_KB` (default **1024**, i.e. 1 MB). The server refuses to start without the required ones.
- Nothing is written to disk in plaintext: the DB is encrypted with `PP_DB_KEY`, and blobs with an HKDF-derived key.
- The password is hashed with **Argon2id** at startup. Device tokens are 32 random bytes (base64url), stored as SHA-256 hashes.
- Login is rate-limited to **5 attempts/min per client IP** and returns 429 beyond that.
- Images are normalized to **PNG**, with a thumbnail that fits within **256×256** (no upscaling).
- History is kept forever; only explicit deletes remove items.
- No third-party network services of any kind.
- JSON: camelCase, enums as lowercase strings, via `ProtocolJson.Options`.

## Review Focus

1. **Text that differs only in line endings** (`"a\r\nb"` vs `"a\nb"`, e.g. copied on Windows and then on Linux) must dedup to one item. Test is in Task 2.
2. **Non-ASCII search** (Croatian `šećer`, emoji) must find the clip, and queries shorter than 3 characters must still match. Tests are in Task 5.
3. **Search input with FTS syntax characters** (`"`, `*`, `-`, `OR`, `(`) must be treated as literal text. It must never return 500. Test is in Task 5.
4. **Restarting with a wrong `PP_DB_KEY`** against an existing database must refuse to start with a clear error, and the existing file must stay byte-for-byte unchanged. Test is in Task 3.
5. **A revoked device** must get 401 on its next API call, and its open WebSocket must be closed immediately. Test is in Task 7.

---

### Task 1: Solution scaffold, configuration, health endpoint

**Files:**
- Create: `PrivatePaste.slnx`, `Directory.Build.props` (net10.0, Nullable, TreatWarningsAsErrors, ImplicitUsings)
- Create: `src/PrivatePaste.Protocol/PrivatePaste.Protocol.csproj` (empty classlib for now)
- Create: `src/PrivatePaste.Server/PrivatePaste.Server.csproj`, `src/PrivatePaste.Server/Program.cs`
- Create: `src/PrivatePaste.Server/Config/ServerOptions.cs`
- Create: `tests/PrivatePaste.Tests/PrivatePaste.Tests.csproj`, `tests/PrivatePaste.Tests/PrivatePasteFactory.cs`, `tests/PrivatePaste.Tests/Config/ServerOptionsTests.cs`, `tests/PrivatePaste.Tests/HealthTests.cs`
- Modify: `.gitignore` (add `bin/`, `obj/`, `*.user`)

**Interfaces:**
- Produces: `public sealed record ServerOptions(string DbKey, string Password, string DataDir, long MaxImageBytes, long MaxTextBytes)` with `public static ServerOptions FromConfiguration(IConfiguration config)`. It reads the keys `PP_DB_KEY`, `PP_PASSWORD`, `PP_DATA_DIR` (default `/data`), `PP_MAX_IMAGE_MB` and `PP_MAX_TEXT_KB`, and throws `InvalidOperationException` with a message naming the missing or invalid variable.
- Produces: `public partial class Program` (for `WebApplicationFactory`). `ServerOptions` is registered as a singleton.
- Produces (tests): `PrivatePasteFactory : WebApplicationFactory<Program>`. It sets `PP_DB_KEY="test-key-0123456789abcdef"` and `PP_PASSWORD="hunter2-test"`, and sets `PP_DATA_DIR` to a fresh temp dir per factory instance, deleted on dispose. Settings are applied with `builder.UseSetting`.

- [ ] **Step 1: Scaffold the solution and projects** with `dotnet new` (classlib, web, xunit), wire them into `PrivatePaste.slnx`, and add a reference from the test project to the server and protocol projects. Run `dotnet build` and expect `Build succeeded`.

- [ ] **Step 2: Write the failing tests**

```csharp
// ServerOptionsTests
[Fact] void Missing_db_key_throws() => Assert.Contains("PP_DB_KEY",
    Assert.Throws<InvalidOperationException>(() => ServerOptions.FromConfiguration(Config(("PP_PASSWORD","x")))).Message);
[Fact] void Missing_password_throws() => /* same, PP_PASSWORD, with PP_DB_KEY = 16+ chars */;
[Fact] void Short_db_key_throws() => /* PP_DB_KEY="short" → message contains "PP_DB_KEY" and "16" */;
[Fact] void Defaults_apply() { var o = ServerOptions.FromConfiguration(Config(("PP_DB_KEY", new string('k',16)), ("PP_PASSWORD","p")));
    Assert.Equal("/data", o.DataDir); Assert.Equal(20L*1024*1024, o.MaxImageBytes); Assert.Equal(1024L*1024, o.MaxTextBytes); }
[Fact] void Overrides_apply() => /* PP_MAX_IMAGE_MB=5, PP_MAX_TEXT_KB=10 → 5*1024*1024, 10*1024 */;

// HealthTests
[Fact] async Task Health_returns_200_without_auth() {
    using var f = new PrivatePasteFactory(); var r = await f.CreateClient().GetAsync("/health");
    Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
```

- [ ] **Step 3: Run `dotnet test --filter "ServerOptionsTests|HealthTests"`** and expect FAIL with compile errors (`ServerOptions` not defined).

- [ ] **Step 4: Implement `ServerOptions.FromConfiguration`** and `Program.cs`, which builds options, registers them, maps `MapHealthChecks("/health")`, and listens on `http://+:8080` unless overridden.

- [ ] **Step 5: Run `dotnet test`** and expect all PASS.

- [ ] **Step 6: Commit** with `git add -A && git commit -m "feat(server): scaffold solution, config validation, health endpoint"`

---

### Task 2: Protocol library — DTOs, JSON options, content hash, link detection

**Files:**
- Create: `src/PrivatePaste.Protocol/Dtos.cs`, `src/PrivatePaste.Protocol/ProtocolJson.cs`, `src/PrivatePaste.Protocol/ContentHash.cs`, `src/PrivatePaste.Protocol/ContentClassifier.cs`
- Test: `tests/PrivatePaste.Tests/Protocol/ContentHashTests.cs`, `tests/PrivatePaste.Tests/Protocol/ContentClassifierTests.cs`, `tests/PrivatePaste.Tests/Protocol/ProtocolJsonTests.cs`

**Interfaces:**
- Produces (all in namespace `PrivatePaste.Protocol`):

```csharp
public enum ClipKind { Text, Link, Image }
public sealed record ClipDto(Guid Id, long Seq, ClipKind Kind, string? Text, string Hash,
    int? Width, int? Height, long SizeBytes, Guid? SourceDeviceId, string SourceDeviceName,
    DateTimeOffset LastCopiedAt);
public sealed record LoginRequest(string Password, string DeviceName, string Platform);
public sealed record LoginResponse(Guid DeviceId, string Token);
public sealed record TextClipUpload(string Text, DateTimeOffset? CopiedAt, bool Backfill);
public sealed record ClipPage(IReadOnlyList<ClipDto> Items, string? NextCursor);
public sealed record SyncResponse(IReadOnlyList<ClipDto> Clips, IReadOnlyList<Guid> DeletedIds, long LatestSeq, bool HasMore);
public sealed record DeviceDto(Guid Id, string Name, string Platform, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool IsCurrent);
// WebSocket envelope. Type is one of WsTypes.*
public sealed record WsMessage(string Type, ClipDto? Clip = null, Guid? Id = null, long? Seq = null, bool? Live = null);
public static class WsTypes { public const string ClipCreated = "clip.created", ClipDeleted = "clip.deleted", Ping = "ping", Pong = "pong"; }
public static class ProtocolJson { public static JsonSerializerOptions Options { get; } }
public static class ContentHash {
    public static string NormalizeText(string text);                      // CRLF and lone CR → LF; nothing else changes
    public static string ForText(string text);                            // hex(SHA256(UTF8("t:" + NormalizeText(text)))), lowercase
    public static string ForPixels(ReadOnlySpan<byte> rgba, int width, int height); // hex(SHA256(UTF8($"i:{width}x{height}:") ++ rgba))
}
public static class ContentClassifier { public static ClipKind ClassifyText(string text); }
```

`ForPixels` takes RGBA8888, **unpremultiplied**, row-major, with no row padding. It is the canonical image identity across all clients, so record that in an XML doc comment.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] void Crlf_and_lf_hash_equal() => Assert.Equal(ContentHash.ForText("a\nb"), ContentHash.ForText("a\r\nb"));
[Fact] void Lone_cr_normalized() => Assert.Equal("a\nb", ContentHash.NormalizeText("a\rb"));
[Fact] void Text_hash_is_lowercase_hex_64() => Assert.Matches("^[0-9a-f]{64}$", ContentHash.ForText("x"));
[Fact] void Text_hash_known_vector() => Assert.Equal(Sha256Hex("t:hello"), ContentHash.ForText("hello"));
[Fact] void Whitespace_is_significant() => Assert.NotEqual(ContentHash.ForText("a"), ContentHash.ForText("a "));
[Fact] void Pixel_hash_depends_on_dimensions() { var px = new byte[8];
    Assert.NotEqual(ContentHash.ForPixels(px,2,1), ContentHash.ForPixels(px,1,2)); }

[Theory, InlineData("https://github.com/dokploy/dokploy", ClipKind.Link), InlineData("  http://x.io/a?b=1  ", ClipKind.Link),
 InlineData("see https://x.io", ClipKind.Text), InlineData("ftp://x.io", ClipKind.Text),
 InlineData("https://a.io\nhttps://b.io", ClipKind.Text), InlineData("github.com", ClipKind.Text)]
void Classifies(string text, ClipKind expected) => Assert.Equal(expected, ContentClassifier.ClassifyText(text));

[Fact] void Kind_serializes_lowercase() => Assert.Contains("\"kind\":\"link\"",
    JsonSerializer.Serialize(SampleClip(ClipKind.Link), ProtocolJson.Options));
[Fact] void WsMessage_roundtrips() => /* serialize + deserialize clip.created message, assert equal Type, Clip.Id, Live */;
```

- [ ] **Step 2: Run `dotnet test --filter "FullyQualifiedName~Protocol"`** and expect FAIL with compile errors.

- [ ] **Step 3: Implement the types.** `ClassifyText` returns Link when the trimmed text contains no whitespace and `Uri.TryCreate(..., UriKind.Absolute)` succeeds with scheme `http` or `https`. `ProtocolJson.Options` uses `JsonSerializerDefaults.Web` plus `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`.

- [ ] **Step 4: Run the tests again** and expect PASS.

- [ ] **Step 5: Commit** with `git commit -am "feat(protocol): DTOs, JSON options, content hash, link detection"` (stage the new files first).

---

### Task 3: Encrypted database and schema

**Files:**
- Create: `src/PrivatePaste.Server/Storage/PrivatePasteDb.cs`, `src/PrivatePaste.Server/Storage/Schema.sql` (embedded resource)
- Modify: `src/PrivatePaste.Server/PrivatePaste.Server.csproj` (add `Microsoft.Data.Sqlite.Core`, `SQLite3MC.PCLRaw.bundle`, pin the restored versions), `Program.cs` (call `Initialize()` at startup)
- Test: `tests/PrivatePaste.Tests/Storage/PrivatePasteDbTests.cs`

**Interfaces:**
- Consumes: `ServerOptions` (Task 1).
- Produces: `public sealed class PrivatePasteDb(ServerOptions options)` with `public SqliteConnection Open()`, which returns an opened connection with the key applied and `PRAGMA foreign_keys=ON`. It also has `public void Initialize()`, which creates the directory, verifies the key and applies the schema. It throws `DatabaseKeyMismatchException` if an existing file can't be read with the key. The DB path is `{DataDir}/privatepaste.db` and blobs go in `{DataDir}/blobs/`.
- Produces: the schema, used by Tasks 4–5:

```sql
CREATE TABLE IF NOT EXISTS counters(name TEXT PRIMARY KEY, value INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, platform TEXT NOT NULL,
  token_hash TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL, last_seen_at TEXT, revoked_at TEXT);
CREATE TABLE IF NOT EXISTS clips(id TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, kind TEXT NOT NULL, text TEXT,
  hash TEXT NOT NULL UNIQUE, width INTEGER, height INTEGER, size_bytes INTEGER NOT NULL,
  source_device_id TEXT, source_device_name TEXT NOT NULL, created_at TEXT NOT NULL, last_copied_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_clips_order ON clips(last_copied_at DESC, seq DESC);
CREATE VIRTUAL TABLE IF NOT EXISTS clips_fts USING fts5(text, content='clips', content_rowid='rowid', tokenize='trigram');
-- + AFTER INSERT / AFTER DELETE / AFTER UPDATE OF text triggers keeping clips_fts in sync (standard external-content pattern)
CREATE TABLE IF NOT EXISTS tombstones(clip_id TEXT PRIMARY KEY, seq INTEGER NOT NULL);
```

Timestamps are stored as ISO-8601 UTC round-trip strings (`"O"`), so they sort as text.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] void Fts5_trigram_available() { /* Initialize, insert into clips, query clips_fts MATCH '"ece"' → 1 row */ }
[Fact] void File_contains_no_plaintext() { /* Initialize, insert clip text "supersecret-canary", close + ClearAllPools,
    Assert.DoesNotContain("supersecret-canary", Encoding.UTF8.GetString(File.ReadAllBytes(path))) */ }
[Fact] void Wrong_key_throws_and_leaves_file_untouched() {
    // Initialize with key A; insert a row; snapshot SHA256(file bytes)
    // new PrivatePasteDb(options with key B).Initialize() → Assert.Throws<DatabaseKeyMismatchException>
    // Assert.Equal(snapshot, SHA256(file bytes))
}
[Fact] void Initialize_is_idempotent() { /* call twice, no throw */ }
```

- [ ] **Step 2: Run `dotnet test --filter PrivatePasteDbTests`** and expect FAIL.

- [ ] **Step 3: Implement `PrivatePasteDb`.** Call `SQLitePCL.Batteries_V2.Init()` once (static ctor). Build the connection string with `SqliteConnectionStringBuilder { DataSource, Password = DbKey, Mode = ReadWriteCreate }`. In `Initialize`, if the file exists, open it and run `SELECT count(*) FROM sqlite_master` before any write. If that throws `SqliteException`, wrap it in `DatabaseKeyMismatchException("PP_DB_KEY does not match the existing database at {path}")`. Otherwise run `Schema.sql`.

- [ ] **Step 4: Run the tests again** and expect PASS. If `Fts5_trigram_available` fails, spec risk #3 has happened. Stop and report rather than working around it.

- [ ] **Step 5: Make `Program.cs` call `Initialize()`** before `app.Run()`. On `DatabaseKeyMismatchException`, log a critical message and exit with code 1. Run `dotnet test` and expect all PASS.

- [ ] **Step 6: Commit** with `git commit -m "feat(server): encrypted SQLite database with FTS5 schema"`

---

### Task 4: Encrypted blob store and image processing

**Files:**
- Create: `src/PrivatePaste.Server/Storage/BlobStore.cs`, `src/PrivatePaste.Server/Images/ImageProcessor.cs`
- Modify: server csproj (add `SkiaSharp`, `SkiaSharp.NativeAssets.Linux.NoDependencies`)
- Test: `tests/PrivatePaste.Tests/Storage/BlobStoreTests.cs`, `tests/PrivatePaste.Tests/Images/ImageProcessorTests.cs`

**Interfaces:**
- Consumes: `ServerOptions`, `ContentHash.ForPixels`.
- Produces: `public sealed class BlobStore(ServerOptions options)` with `Task WriteAsync(string name, byte[] plaintext)`, `Task<byte[]?> ReadAsync(string name)` (null if missing) and `void Delete(string name)` (no-op if missing). The key is `HKDF.DeriveKey(SHA256, UTF8(DbKey), 32, info: UTF8("privatepaste-blobs-v1"))`. The file format is `nonce(12) ‖ tag(16) ‖ ciphertext`, using `AesGcm`. A tampered file throws `AuthenticationTagMismatchException` (let it surface).
- Produces: `public sealed record ProcessedImage(byte[] Png, byte[] ThumbnailPng, int Width, int Height, string Hash)`, and `public sealed class ImageProcessor` with `ProcessedImage Process(Stream input)`. It throws `InvalidImageException` when the input can't be decoded. `Hash` is `ContentHash.ForPixels` over the decoded bitmap as `SKColorType.Rgba8888` / `SKAlphaType.Unpremul`.
- Blob names: `{clipId}.png` and `{clipId}.thumb.png` (stored as `{name}.enc` on disk).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] async Task Roundtrip() { await store.WriteAsync("a.png", bytes); Assert.Equal(bytes, await store.ReadAsync("a.png")); }
[Fact] async Task On_disk_bytes_are_not_plaintext() { /* write canary bytes; raw file must not contain them */ }
[Fact] async Task Missing_returns_null() => Assert.Null(await store.ReadAsync("nope"));
[Fact] async Task Tampered_file_throws() { /* flip one ciphertext byte → Assert.ThrowsAnyAsync<CryptographicException> */ }

[Fact] void Jpeg_becomes_png_with_dims() { var r = proc.Process(MakeJpeg(1280, 720));
    Assert.Equal((1280,720), (r.Width, r.Height)); Assert.True(IsPng(r.Png)); }
[Fact] void Thumbnail_fits_256_preserving_aspect() { var r = proc.Process(MakePng(1280,720)); Assert.Equal((256,144), Dims(r.ThumbnailPng)); }
[Fact] void Small_image_not_upscaled() { var r = proc.Process(MakePng(100,50)); Assert.Equal((100,50), Dims(r.ThumbnailPng)); }
[Fact] void Same_pixels_different_encoding_same_hash() => Assert.Equal(proc.Process(MakePng(10,10,seed:1)).Hash, proc.Process(MakeBmp(10,10,seed:1)).Hash);
[Fact] void Garbage_throws() => Assert.Throws<InvalidImageException>(() => proc.Process(new MemoryStream("not an image"u8.ToArray())));
```

Test images are made with SkiaSharp in a `TestImages` helper. Use a lossless format (BMP or WebP-lossless) for the same-pixels test. JPEG is lossy, so it must not be used there.

- [ ] **Step 2: Run `dotnet test --filter "BlobStoreTests|ImageProcessorTests"`** and expect FAIL.

- [ ] **Step 3: Implement both classes.** Downscale the thumbnail with `SKSamplingOptions(SKCubicResampler.Mitchell)`.

- [ ] **Step 4: Run the tests again** and expect PASS.

- [ ] **Step 5: Commit** with `git commit -m "feat(server): encrypted blob store and image normalization"`

---

### Task 5: Clip repository — dedup, ordering, search, paging, sync, delete

**Files:**
- Create: `src/PrivatePaste.Server/Storage/ClipRepository.cs`
- Test: `tests/PrivatePaste.Tests/Storage/ClipRepositoryTests.cs`

**Interfaces:**
- Consumes: `PrivatePasteDb`, `ClipDto`, `ClipPage`, `SyncResponse` (Task 2).
- Produces:

```csharp
public sealed record NewClip(ClipKind Kind, string? Text, string Hash, int? Width, int? Height, long SizeBytes,
    Guid? SourceDeviceId, string SourceDeviceName, DateTimeOffset CopiedAt);
public sealed record UpsertResult(ClipDto Clip, bool Created);
public sealed class ClipRepository(PrivatePasteDb db) {
    Task<UpsertResult> UpsertAsync(NewClip clip);          // new hash → insert; existing hash → bump
    Task<ClipDto?> BumpAsync(Guid id, Guid? deviceId, string deviceName, DateTimeOffset at);
    Task<ClipDto?> GetAsync(Guid id);
    Task<ClipPage> ListAsync(string? query, string? cursor, int limit); // limit clamped 1..200
    Task<long?> DeleteAsync(Guid id);                       // returns tombstone seq, null if not found
    Task<SyncResponse> SyncAsync(long afterSeq, int limit); // limit clamped 1..500
}
```

Rules:
- Every insert, bump and delete takes the next value of `counters('seq')` inside the same transaction. Writes are serialized with a `SemaphoreSlim(1,1)`.
- A bump sets `last_copied_at = max(existing, at)`, updates the source device fields and assigns a new seq. The id stays the same.
- Ordering is `last_copied_at DESC, seq DESC`. The cursor is an opaque `"{lastCopiedAt:O}|{seq}"`, base64url-encoded.
- Search uses FTS5 `MATCH` when the query is ≥ 3 characters. The query is escaped by wrapping it in double quotes and doubling any embedded `"`, so FTS operators are literal. For 1–2 characters use `text LIKE '%' || @q || '%' ESCAPE '\'`, escaping `%`, `_` and `\`. Search is case-insensitive for ASCII. Image clips never match a non-empty query.
- Deleting removes the row and inserts a tombstone. `SyncAsync` returns clips with `seq > afterSeq` ordered by seq ascending, the tombstones with `seq > afterSeq`, `LatestSeq` = the current counter, and `HasMore`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] async Task Same_hash_bumps_not_duplicates() { var a = await Upsert("x", device:"PC-work", at:T0);
    var b = await Upsert("x", device:"Phone", at:T0.AddMinutes(1));
    Assert.False(b.Created); Assert.Equal(a.Clip.Id, b.Clip.Id); Assert.True(b.Clip.Seq > a.Clip.Seq);
    Assert.Equal("Phone", b.Clip.SourceDeviceName); Assert.Single((await repo.ListAsync(null,null,50)).Items); }
[Fact] async Task Bump_never_moves_time_backwards() { /* upsert at T1, then same hash at T0 < T1 → LastCopiedAt == T1 */ }
[Fact] async Task List_orders_by_last_copied_desc() { /* a@T0, b@T2, c@T1 → [b, c, a] */ }
[Fact] async Task Paging_with_cursor_covers_all_without_overlap() { /* 7 clips, limit 3 → 3+3+1, NextCursor null on last, ids distinct */ }
[Fact] async Task Search_trigram_substring() { /* "docker compose up -d" found by "compose" */ }
[Fact] async Task Search_non_ascii() { /* "kupiti šećer i mlijeko" found by "šećer"; "deploy 🚀 done" found by "🚀" */ }
[Fact] async Task Search_short_query_uses_like() { /* "ab" finds "xaby"; "%" finds only text containing a literal % */ }
[Theory, InlineData("\"unclosed"), InlineData("a OR b"), InlineData("-x*"), InlineData("(foo")]
async Task Search_with_fts_syntax_is_literal(string q) { /* no throw; clip whose text contains q exactly is found; others not */ }
[Fact] async Task Search_case_insensitive_ascii() { /* "Docker" finds "docker" */ }
[Fact] async Task Delete_creates_tombstone_seen_by_sync() { /* insert a,b; s = LatestSeq; delete a →
    SyncAsync(s) has DeletedIds==[a.Id], Clips empty */ }
[Fact] async Task Sync_returns_changes_after_seq_in_seq_order() { /* insert a,b; s; bump a; insert c → Clips ids [a, c] */ }
[Fact] async Task Sync_has_more_when_limited() { /* 5 clips, SyncAsync(0, 2) → 2 clips, HasMore true */ }
```

- [ ] **Step 2: Run `dotnet test --filter ClipRepositoryTests`** and expect FAIL.

- [ ] **Step 3: Implement `ClipRepository`** with plain ADO.NET (`SqliteCommand`, parameters only, no string concatenation of user input).

- [ ] **Step 4: Run the tests again** and expect PASS.

- [ ] **Step 5: Commit** with `git commit -m "feat(server): clip repository with dedup, search, paging, sync"`

---

### Task 6: Device auth and the clip REST API

**Files:**
- Create: `src/PrivatePaste.Server/Auth/PasswordVerifier.cs`, `src/PrivatePaste.Server/Auth/DeviceRepository.cs`, `src/PrivatePaste.Server/Auth/DeviceTokenAuthHandler.cs`
- Create: `src/PrivatePaste.Server/Clips/ClipService.cs`, `src/PrivatePaste.Server/Clips/IClipBroadcaster.cs`
- Create: `src/PrivatePaste.Server/Api/AuthEndpoints.cs`, `src/PrivatePaste.Server/Api/ClipEndpoints.cs`, `src/PrivatePaste.Server/Api/DeviceEndpoints.cs`
- Modify: `Program.cs` (auth schemes, rate limiter, forwarded headers, Kestrel `MaxRequestBodySize = MaxImageBytes + 1 MB`, `TimeProvider.System`)
- Test: `tests/PrivatePaste.Tests/Api/AuthApiTests.cs`, `tests/PrivatePaste.Tests/Api/ClipApiTests.cs`; extend `PrivatePasteFactory` with `Task<HttpClient> LoginAsync(string deviceName = "PC-test")` and a `FakeTimeProvider` override.

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces:
  - `PasswordVerifier(ServerOptions)`: Argon2id-hashes `Password` with a random salt at construction (memory 64 MB, iterations 3, parallelism 1). `bool Verify(string candidate)` compares in fixed time.
  - `DeviceRepository(PrivatePasteDb, TimeProvider)`: `Task<(DeviceDto Device, string Token)> CreateAsync(string name, string platform)`, `Task<DeviceDto?> FindByTokenAsync(string token)` (excludes revoked devices, updates `last_seen_at` at most once per minute), `Task<IReadOnlyList<DeviceDto>> ListAsync(Guid? currentId)` (excludes revoked), `Task<bool> RevokeAsync(Guid id)`.
  - Auth scheme `"DeviceToken"` reads `Authorization: Bearer <token>` and sets claims `sub` = device id and `name` = device name. Authorization policy `"Owner"` accepts the schemes `DeviceToken` and `Cookies`. The Cookies scheme is configured in Task 8; register it here with `OnRedirectToLogin` returning 401 for paths that start with `/api`.
  - `public sealed record DeviceIdentity(Guid? Id, string Name)`. The web UI uses `(null, "Web")`.
  - `public interface IClipBroadcaster { Task ClipChangedAsync(ClipDto clip, bool live); Task ClipDeletedAsync(Guid id, long seq); }`. Register `NullClipBroadcaster` now; Task 7 replaces it.
  - `ClipService(ClipRepository, BlobStore, ImageProcessor, IClipBroadcaster, ServerOptions, TimeProvider)` with `Task<ClipDto> AddTextAsync(TextClipUpload, DeviceIdentity)`, `Task<ClipDto> AddImageAsync(Stream, DateTimeOffset? copiedAt, bool backfill, DeviceIdentity)`, `Task<ClipDto?> RestoreAsync(Guid id, DeviceIdentity)` and `Task<bool> DeleteAsync(Guid id)`. Validation errors throw `ClipRejectedException(int statusCode, string message)`, which the endpoints map to that status with a `ProblemDetails` body.
    - Text that is empty or whitespace-only gets 400. Text with UTF-8 byte length > `MaxTextBytes` gets 413. Text is stored normalized (`ContentHash.NormalizeText`), and its kind comes from `ContentClassifier`.
    - An image > `MaxImageBytes` gets 413, checked while reading the stream and before decoding. An undecodable image gets 400. A new image writes both blobs. If the upsert dedups, the just-written blobs aren't needed: delete them or skip writing them.
    - Time: live clips use `TimeProvider.GetUtcNow()` and ignore the client `copiedAt`. Backfill clips use `min(copiedAt ?? now, now)`.
    - Every successful add or restore calls `ClipChangedAsync(clip, live: !backfill)`. Restores are always live. Deletes also remove both blobs and call `ClipDeletedAsync`.
- Endpoints (all under `/api` require policy `Owner`, except login):

| Method & path | Body / query | Success |
|---|---|---|
| `POST /api/auth/login` (rate-limit policy `"login"`) | `LoginRequest` | 200 `LoginResponse`; 401 on wrong password; 400 if `DeviceName` is empty or > 50 chars |
| `POST /api/auth/logout` | — | 204, revokes the calling device |
| `GET /api/devices` | — | 200 `DeviceDto[]` (`IsCurrent` set for the caller) |
| `DELETE /api/devices/{id}` | — | 204; 404 if unknown |
| `POST /api/clips/text` | `TextClipUpload` | 200 `ClipDto` |
| `POST /api/clips/image` | multipart: `file`, optional `copiedAt`, `backfill` | 200 `ClipDto` |
| `GET /api/clips` | `q`, `cursor`, `limit` (default 50) | 200 `ClipPage` |
| `GET /api/clips/{id}` | — | 200 `ClipDto`; 404 |
| `GET /api/clips/{id}/image`, `/thumbnail` | — | 200 `image/png`; 404 if missing or not an image |
| `POST /api/clips/{id}/restore` | — | 200 `ClipDto` (bumped, live); 404 |
| `DELETE /api/clips/{id}` | — | 204; 404 |
| `GET /api/sync` | `afterSeq` (default 0), `limit` (default 200) | 200 `SyncResponse` |

The rate limiter is a fixed window of 5 permits per minute, partitioned by `HttpContext.Connection.RemoteIpAddress`, with no queue, and `RejectionStatusCode = 429`. Use `UseForwardedHeaders` with `XForwardedFor | XForwardedProto`, `KnownNetworks`/`KnownProxies` cleared and `ForwardLimit = 1`. The container is only reachable through Dokploy's Traefik; document that in the README (Task 9).

- [ ] **Step 1: Write the failing tests**

```csharp
// AuthApiTests
[Fact] Login_with_correct_password_returns_token  // token non-empty; GET /api/clips with bearer → 200
[Fact] Login_with_wrong_password_401
[Fact] Login_rate_limited_after_5_attempts        // 5 wrong → 401 each; 6th → 429
[Fact] Api_without_token_401                       // GET /api/clips no auth → 401 (not 302)
[Fact] Garbage_token_401
[Fact] Device_list_marks_current                   // login A, login B; as B: 2 devices, B.IsCurrent true
[Fact] Logout_revokes_own_token                    // logout → 204; same token → 401
[Fact] Revoked_device_gets_401                     // as A: DELETE /api/devices/{B} → 204; B's next GET /api/clips → 401
[Fact] Device_name_too_long_400                    // 51 chars

// ClipApiTests
[Fact] Text_upload_roundtrip_and_kind              // "https://x.io" → kind link; listed first
[Fact] Crlf_text_stored_normalized                 // upload "a\r\nb" → Text == "a\nb"; then "a\nb" → same Id
[Theory, InlineData(""), InlineData("   \n\t")] Empty_text_400
[Fact] Text_over_limit_413                          // factory with PP_MAX_TEXT_KB=1; 1025 ASCII bytes → 413; 1024 → 200
[Fact] Image_upload_returns_dims_and_serves_png_and_thumbnail
[Fact] Same_image_twice_dedups                      // same Id, Seq increases
[Fact] Garbage_image_400
[Fact] Image_over_limit_413                         // PP_MAX_IMAGE_MB=1; 1 MB + 1 byte payload → 413
[Fact] Live_upload_ignores_client_time              // fake clock T; copiedAt = T-1h, backfill false → LastCopiedAt == T
[Fact] Backfill_keeps_client_time_clamped_to_now    // copiedAt = T-1h → T-1h; copiedAt = T+1h → T
[Fact] Restore_bumps_to_top                         // a, b; restore a → list [a, b]
[Fact] Delete_removes_clip_and_blobs                // image clip; delete → 204; GET image → 404; sync shows DeletedIds
[Fact] Search_query_param_works                     // q=compose
```

- [ ] **Step 2: Run `dotnet test --filter "AuthApiTests|ClipApiTests"`** and expect FAIL.

- [ ] **Step 3: Implement the auth pieces** (`PasswordVerifier`, `DeviceRepository`, `DeviceTokenAuthHandler`, the policy, the rate limiter and forwarded headers) and the auth/device endpoints. Run `dotnet test --filter AuthApiTests` and expect PASS.

- [ ] **Step 4: Implement `ClipService`, `NullClipBroadcaster` and the clip endpoints.** Run `dotnet test --filter ClipApiTests` and expect PASS.

- [ ] **Step 5: Run the full suite with `dotnet test`** and expect all PASS.

- [ ] **Step 6: Commit** with `git commit -m "feat(server): device auth, rate-limited login, clip REST API"`

---

### Task 7: WebSocket push and revocation

**Files:**
- Create: `src/PrivatePaste.Server/Realtime/ConnectionHub.cs`, `src/PrivatePaste.Server/Realtime/WebSocketEndpoint.cs`
- Modify: `Program.cs` (`UseWebSockets(KeepAliveInterval = 30s)`, register `ConnectionHub` as `IClipBroadcaster`), `Api/DeviceEndpoints.cs` and `/api/auth/logout` (close sockets on revoke)
- Test: `tests/PrivatePaste.Tests/Realtime/WebSocketTests.cs`

**Interfaces:**
- Consumes: `IClipBroadcaster`, `DeviceRepository`, `WsMessage`, `ProtocolJson.Options`.
- Produces: `ConnectionHub : IClipBroadcaster` with `Task RunAsync(Guid deviceId, WebSocket socket, CancellationToken ct)` (registers the socket, runs the receive loop, unregisters on exit) and `Task CloseDeviceAsync(Guid deviceId)` (closes with `WebSocketCloseStatus.PolicyViolation`, description `"revoked"`).
- `GET /ws` requires the `DeviceToken` scheme, so the bearer goes in the upgrade request header. Non-WebSocket requests get 400.
- Server → client: `{"type":"clip.created","clip":{…},"live":true|false}` and `{"type":"clip.deleted","id":"…","seq":N}`. The server broadcasts to **all** connected sockets, including the sender's device; clients ignore clips whose `sourceDeviceId` is their own. Client → server: `{"type":"ping"}` gets `{"type":"pong"}`. Anything else is ignored.
- Each socket gets its own send lock (`SemaphoreSlim`). If a send fails, drop that socket and don't fail the broadcast.

- [ ] **Step 1: Write the failing tests** using `factory.Server.CreateWebSocketClient()`, with `ConfigureRequest` adding the bearer header.

```csharp
[Fact] Connect_without_token_rejected                 // ConnectAsync throws (401)
[Fact] Text_upload_is_pushed_live_to_other_device     // B connected; A uploads "hi" → B receives clip.created, Live true, Clip.Text "hi"
[Fact] Backfill_upload_pushed_with_live_false
[Fact] Delete_is_pushed                               // clip.deleted with matching Id
[Fact] Ping_gets_pong
[Fact] Revoking_device_closes_its_socket              // B connected; A deletes device B → B's ReceiveAsync returns Close, CloseStatusDescription "revoked"
[Fact] Logout_closes_own_socket
[Fact] Dead_socket_does_not_break_broadcast           // B connects then aborts; C connected; A uploads → C still receives
```

Receives in tests use a 5 s timeout `CancellationTokenSource`, so a missing push fails instead of hanging.

- [ ] **Step 2: Run `dotnet test --filter WebSocketTests`** and expect FAIL.

- [ ] **Step 3: Implement `ConnectionHub` and the endpoint, and wire revocation.**

- [ ] **Step 4: Run `dotnet test`** and expect all PASS.

- [ ] **Step 5: Commit** with `git commit -m "feat(server): WebSocket push, ping/pong, close on revoke"`

---

### Task 8: Minimal web UI

**Files:**
- Create: `src/PrivatePaste.Server/Pages/_ViewImports.cshtml`, `Pages/Shared/_Layout.cshtml`, `Pages/Login.cshtml(.cs)`, `Pages/Index.cshtml(.cs)`, `Pages/Devices.cshtml(.cs)`, `wwwroot/site.css`, `wwwroot/site.js`
- Modify: `Program.cs` (`AddRazorPages` with the `Owner` policy on the folder and `Login` anonymous; Cookies scheme: `LoginPath="/login"`, `HttpOnly`, `SecurePolicy=SameAsRequest`, `SameSite=Strict`, sliding expiry 30 days)
- Test: `tests/PrivatePaste.Tests/Web/WebUiTests.cs`

**Interfaces:**
- Consumes: `PasswordVerifier`, `ClipService`, `ClipRepository`, `DeviceRepository`, `ConnectionHub.CloseDeviceAsync`, and the `"login"` rate-limit policy (applied to the Login POST).
- Behavior:
  - **Login:** a password field. Success signs in the cookie and redirects to `/`. A wrong password re-renders with "Wrong password." The page is rate-limited.
  - **Index:** a search box (`?q=`) and a list of 50 items with a "Load more" link (`?cursor=`). Each row shows a kind icon, a text preview (first 200 chars, HTML-encoded; links rendered as `<a rel="noopener noreferrer" target="_blank">`) or a thumbnail `<img src="/api/clips/{id}/thumbnail">`, the source device name and relative time.
  - Each row has **Copy** and **Delete** buttons. Copy calls `POST /api/clips/{id}/restore` with the cookie (this re-syncs, per the spec) and then copies to the browser clipboard: text via `navigator.clipboard.writeText`, images via fetch → `ClipboardItem`. Delete posts a form handler.
  - **Devices:** a table of name, platform, created and last-seen dates, with a Revoke button per device that calls `RevokeAsync` and `CloseDeviceAsync`. There's also a **Sign out** link that clears the cookie.
  - Cookie-authenticated `POST`/`DELETE` calls to `/api` must carry an antiforgery token. Validate the `RequestVerificationToken` header for cookie-authenticated non-GET API calls; bearer-authenticated calls are exempt.

- [ ] **Step 1: Write the failing tests.** Use a `CookieContainer` client with `AllowAutoRedirect = false`, and a helper that GETs a page and extracts `__RequestVerificationToken` from the HTML.

```csharp
[Fact] Index_redirects_to_login_when_anonymous        // GET / → 302 Location /login
[Fact] Login_wrong_password_shows_error               // 200, body contains "Wrong password."
[Fact] Login_then_index_lists_clips                   // upload "hello web" via API; login via form; GET / contains "hello web"
[Fact] Index_html_encodes_clip_text                   // clip "<script>alert(1)</script>" → body contains "&lt;script&gt;"
[Fact] Index_search_filters                           // ?q=compose
[Fact] Delete_from_web_removes_clip
[Fact] Restore_via_cookie_requires_antiforgery        // without header → 400; with token → 200
[Fact] Devices_page_revoke_works                      // device token stops working (401)
```

- [ ] **Step 2: Run `dotnet test --filter WebUiTests`** and expect FAIL.

- [ ] **Step 3: Implement the pages, CSS and JS.** Keep it compact and readable, in both light and dark mode (`prefers-color-scheme`), with no external CDN assets.

- [ ] **Step 4: Run `dotnet test`** and expect all PASS.

- [ ] **Step 5: Manual check:** run `PP_DB_KEY=dev-key-0123456789ab PP_PASSWORD=dev PP_DATA_DIR=./.devdata dotnet run --project src/PrivatePaste.Server`, open `http://localhost:8080`, log in, add a clip with curl, and confirm it shows, copies and deletes. Add `.devdata/` to `.gitignore`.

- [ ] **Step 6: Commit** with `git commit -m "feat(server): minimal web UI for history and devices"`

---

### Task 9: Docker image, compose file, README

**Files:**
- Create: `Dockerfile`, `.dockerignore`, `docker-compose.yml`, `README.md`

**Interfaces:**
- Dockerfile: multi-stage. Build with `mcr.microsoft.com/dotnet/sdk:10.0` (`dotnet publish src/PrivatePaste.Server -c Release -o /app`), then run on `mcr.microsoft.com/dotnet/aspnet:10.0`. `RUN mkdir /data && chown app:app /data`, `USER app`, `VOLUME /data`, `EXPOSE 8080`, `ENV ASPNETCORE_HTTP_PORTS=8080`, `HEALTHCHECK` (curl isn't in the image, so leave the health check to Dokploy using `/health`).
- docker-compose.yml: one service `privatepaste` built from `.`, a named volume `privatepaste-data:/data`, `environment: PP_DB_KEY: ${PP_DB_KEY:?}` and `PP_PASSWORD: ${PP_PASSWORD:?}`, and `expose: ["8080"]`. Don't publish a host port; Dokploy's Traefik routes to it.
- README sections:
  - What it is.
  - Deploy on Dokploy: create a Compose app from the repo, set the env vars, add a domain on port 8080 with HTTPS. Traefik passes WebSockets through by default.
  - Generating keys (`openssl rand -base64 32`).
  - **Back up `PP_DB_KEY` — without it the data is unrecoverable.**
  - Changing the password: edit the env var and redeploy.
  - Backups: copy the volume and keep it with the key.
  - Forwarded-header trust note (only expose the container through the proxy).
  - Local dev command.

- [ ] **Step 1: Write the files.**

- [ ] **Step 2: Run `dotnet publish src/PrivatePaste.Server -c Release -r linux-x64 --self-contained false -o ./.publish-check`** and expect success, with `libe_sqlite3mc.so` (or the bundle's Linux native) and `libSkiaSharp.so` in the output. Then delete `.publish-check`.

- [ ] **Step 3: Run `docker build -t privatepaste .`**, then `docker run --rm -e PP_DB_KEY=0123456789abcdef0123 -e PP_PASSWORD=x -p 8080:8080 privatepaste`, and check that `curl localhost:8080/health` returns `Healthy`. Docker isn't installed on this dev machine. If that's still the case, skip this step, say so in the handoff, and the owner verifies it on Dokploy.

- [ ] **Step 4: Run the full suite with `dotnet test`** and expect all PASS.

- [ ] **Step 5: Commit** with `git commit -m "chore: Dockerfile, compose file for Dokploy, README"`
