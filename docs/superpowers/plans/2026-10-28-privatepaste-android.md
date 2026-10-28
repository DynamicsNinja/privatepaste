# PrivatePaste Android Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A sideloaded Android app that receives clips from the owner's other devices onto the phone's clipboard instantly, sends the phone's clipboard or a shared item in one tap, and shows the searchable history. Two server fixes it depends on come first.

**Architecture:** A native Kotlin app in `android/` (single `:app` Gradle module). Plain-Kotlin packages (`core`, `api`, `storage`, `sync`, `send`) hold all the logic and are tested on the JVM. Thin Android layers sit on top: the foreground service, notifications, clipboard, the share target, the Quick Settings tile and the Compose UI. The sync rules are a port of the desktop's `src/PrivatePaste.Client.Core` (`SyncEngine.cs`, `HistoryCache.cs`, `OutboundQueue.cs`, `ClipboardCoordinator.cs`). Implementers read the C# file named in their task as the reference behaviour, and the rules listed here override it where they differ.

**Tech Stack:**
- Kotlin, Gradle Kotlin DSL with a version catalog, Android Gradle Plugin, Jetpack Compose (Material 3).
- kotlinx.coroutines, kotlinx.serialization (JSON), OkHttp for REST and WebSocket.
- Tests: JUnit 4, Robolectric, OkHttp MockWebServer, kotlinx-coroutines-test.
- Server task: .NET 10 + SkiaSharp (existing).

**Spec:** docs/superpowers/specs/2026-10-28-privatepaste-design.md (§6.3 Android, §6.4 auth, §6.5 sync rules, §7 risk 1). The API contract is the API section of `README.md` (binding).

## Global Constraints

- `applicationId` / namespace `xyz.ficdev.privatepaste` (same id as the macOS bundle).
- minSdk **29**. targetSdk and compileSdk **36**, or newer stable if the chosen AGP requires it.
- Use the latest stable AGP, Gradle, Kotlin and Compose BOM that resolve at implementation time, pinned in `android/gradle/libs.versions.toml`.
- Distributed as a signed APK from GitHub releases (tag `android-v*`). No Play Store. No third-party services in the data path: no Firebase/FCM, no analytics, no crash reporters. The app talks only to the configured server.
- Server URL must be `https://`. Plain `http://` is allowed only for `localhost`, `127.0.0.1` and `[::1]`.
- Sign-in sends `platform = "android"`. The device name defaults to `Build.MODEL`, 1–50 chars.
- Token storage: AES-256-GCM with a non-exportable Android Keystore key. `android:allowBackup="false"`.
- Foreground service type **`specialUse`**, with a permanent low-priority notification "PrivatePaste connected". The app asks for the battery-optimization exemption after the first sign-in.
- Delivery setting: **Automatic** (`setPrimaryClip` from the service) or **Notification** (a heads-up notification per clip with a **Copy** action). Default Automatic.
- Images on the clipboard: a `content://` URI from this app's `FileProvider`, MIME `image/png`.
- Size limits before upload: text **1 MB** (UTF-8 bytes), images **20 MB**. Larger items are not uploaded, and a toast says "Clip too large to sync (text over 1 MB)." or "Clip too large to sync (image over 20 MB)."
- Passwords and one-time codes are treated like any other clip (owner decision). Never set `ClipDescription.EXTRA_IS_SENSITIVE`.
- Reconnect backoff: 1 s doubling to a 30 s cap, ±20% jitter. Never reconnect after a 1008 close or a 401. Show signed-out instead.
- Content hashes must reproduce the README test vectors exactly.
- Branding: Night vault tokens from `branding/README.md`, dark by default with light following the system setting, the Sora font (OFL), and the app icon from `branding/app-icon.svg`.
- Local cache: the most recent **200** clips plus their thumbnails, encrypted at rest with a Keystore key.
- Agent checks run against a LOCAL server only: `PP_PASSWORD=dev`, `PP_DB_KEY=dev-key-0123456789abcdef`, and a temp `PP_DATA_DIR`. Never sign in to https://paste.ficdev.xyz and never use the production password. Agents do not drive a phone or emulator. On-device checks go into the owner checklist.
- Commit messages end with the attribution lines from the agent's own harness reminder.

## Review Focus

1. **A phone photo with EXIF rotation** (shared from the camera or gallery) must appear upright on the PC, with the same hash as the upright pixels. Test: Task 1 `Exif_rotated_jpeg_is_normalized_upright`.
2. **A phone that was offline or dozing for hours** must reconnect and catch up. Only the newest missed clip from another device may reach the clipboard, never an older clip over a newer local send. Queued sends upload in order, as backfill. Tests: Task 5 `catchUpAppliesOnlyNewest`, `catchUpSkippedWhenQueueNotEmpty`; Task 7 `queuedSendsFlushInOrderAsBackfill`.
3. **A phone revoked from the web UI while the app runs** must stop the service, post "Signed out. Sign in again to keep syncing." once, and never retry. Tests: Task 5 `close1008StopsForGood`, `http401StopsForGood`; Task 6 `revokedStopsServiceAndNotifiesOnce`.
4. **Huge shares** (a 30 MB video-sized image, or a 5 MB text) must be refused with the size toast, without reading more than limit + 1 bytes into memory. Test: Task 7 `oversizeImageIsRefusedWithoutReadingWholeStream`.
5. **A Keystore key that is gone or invalid** (app data restored, or a lock-screen change on some OEMs) must show the sign-in screen cleanly, not crash. A corrupt cache file must start empty. Tests: Task 4 `unreadableSessionLoadsAsSignedOut`, `corruptCacheStartsEmpty`.

---

## Task graph (for parallel execution)

`T1 ‖ T2` → `T3 ‖ T4` → `T5 ‖ T7 ‖ T8` → `T6` → `T9`

T1 is server-only and touches nothing under `android/`. Tasks in one wave touch disjoint packages. Each task's Interfaces block is the contract its neighbours code against.

---

### Task 1: Server — EXIF orientation and decoded-pixel cap

**Files:**
- Modify: `src/PrivatePaste.Server/Images/ImageProcessor.cs`
- Modify: `src/PrivatePaste.Server/Api/ClipEndpoints.cs` (map the new exception to 413)
- Test: `tests/PrivatePaste.Tests/Images/ImageProcessorTests.cs`, `tests/PrivatePaste.Tests/Images/TestImages.cs`, `tests/PrivatePaste.Tests/Api/` (one endpoint test)
- Modify: `README.md` (image upload section: one line on orientation and the 50 MP limit)

**Interfaces:**
- Produces: `public sealed class ImageTooLargeException(string message) : Exception(message)`, thrown by `ImageProcessor.Process` when `codec.Info.Width * (long)codec.Info.Height > 50_000_000`. The check runs before any pixel decode. `POST /api/clips/image` maps it to **413** with the problem detail "Image too large (over 50 megapixels)."
- `Process` applies `codec.EncodedOrigin` (all 8 EXIF orientations) before hashing, PNG encoding and the thumbnail. Width, height and hash describe the upright image.

- [ ] **Step 1: Write the failing tests.**
  - `Exif_rotated_jpeg_is_normalized_upright`: build a 2×1 JPEG with EXIF orientation 6 (rotate 90° CW) via a small test helper that writes an APP1 segment. Expect `Width == 1`, `Height == 2`, and `Hash == ContentHash.ForPixels(<upright pixels>)`.
  - `Every_exif_orientation_maps_to_upright`: a theory over orientations 1–8 using a 3×2 image with distinct corner colours. Assert the top-left pixel colour after processing.
  - `Image_over_50_megapixels_is_rejected_before_decoding`: a PNG header claiming 10000×5001 (a tiny file with a crafted IHDR is fine, since decode must not be reached). Expect `ImageTooLargeException`.
  - Endpoint test `Upload_over_50_megapixels_returns_413`.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~ImageProcessor|FullyQualifiedName~Upload_over_50"`. Expected: FAIL.
- [ ] **Step 3:** Implement. Decode as today, then draw onto a canvas with the transform for `SKEncodedOrigin`. For origins 5–8, swap the width and height. Cap check first.
- [ ] **Step 4:** Run the full `dotnet test`. Expected: all pass, 0 warnings.
- [ ] **Step 5:** Commit `fix(server): honor EXIF orientation; reject images over 50 MP`.

### Task 2: Android project scaffold, theme, CI

**Files:**
- Create: `android/settings.gradle.kts`, `android/build.gradle.kts`, `android/gradle.properties`, `android/gradle/libs.versions.toml`, the Gradle wrapper (`gradlew`, `gradlew.bat`, `gradle/wrapper/*`), `android/app/build.gradle.kts`, `android/app/proguard-rules.pro`
- Create: `android/app/src/main/AndroidManifest.xml` (application only: `allowBackup=false`, theme, `MainActivity` launcher)
- Create: `android/app/src/main/java/xyz/ficdev/privatepaste/MainActivity.kt` (Compose, shows the app name for now), `ui/theme/{Color,Type,Theme}.kt`
- Create: `android/app/src/main/res/font/sora.ttf` (variable or 400/600 static, from the Sora OFL release; include `OFL.txt` in `android/app/src/main/assets/licenses/`)
- Create: launcher icon (adaptive):
  - `mipmap-anydpi-v26/ic_launcher.xml` with a background colour `#221D40`.
  - Keyhole foreground as a vector drawable taken from `branding/app-icon.svg`, colour `#B7A5FF`.
  - A monochrome layer.
  - `drawable/ic_notification.xml`: a white keyhole, 24 dp.
- Create: `android/app/src/main/java/xyz/ficdev/privatepaste/core/model/Protocol.kt` (DTOs below, so T3 and T4 can run in parallel)
- Create: `android/app/src/test/java/xyz/ficdev/privatepaste/core/model/ProtocolJsonTest.kt`
- Create: `.github/workflows/android.yml`
- Modify: `.gitignore` (`android/.gradle`, `android/build`, `android/app/build`, `android/local.properties`, `android/keystore/`)

**Interfaces (Produces):**
```kotlin
// core/model/Protocol.kt
@Serializable enum class ClipKind { @SerialName("text") TEXT, @SerialName("link") LINK, @SerialName("image") IMAGE }
@Serializable data class ClipDto(val id: String, val seq: Long, val kind: ClipKind, val text: String? = null,
    val hash: String, val width: Int? = null, val height: Int? = null, val sizeBytes: Long,
    val sourceDeviceId: String, val sourceDeviceName: String? = null,
    @Serializable(InstantSerializer::class) val lastCopiedAt: Instant)          // java.time.Instant, ISO 8601
@Serializable data class ClipPage(val items: List<ClipDto>, val nextCursor: String? = null)
@Serializable data class SyncPage(val clips: List<ClipDto>, val deletedIds: List<String>, val latestSeq: Long, val hasMore: Boolean)
@Serializable data class LoginResponse(val deviceId: String, val token: String)
sealed interface WsEvent { data class ClipCreated(val clip: ClipDto, val live: Boolean) : WsEvent
    data class ClipDeleted(val id: String, val seq: Long) : WsEvent; data object Pong : WsEvent; data object Unknown : WsEvent }
object ProtocolJson { val json: Json /* ignoreUnknownKeys, explicitNulls=false */; fun parseWs(text: String): WsEvent }
```
Theme: `PrivatePasteTheme(content)` with dark and light `ColorScheme`s built from the Night vault tokens:
- Dark: background `#15122B`, surface `#1D1838`, surfaceVariant `#262046`, outline `#2E2756`, onBackground `#E9E6F7`, onSurfaceVariant `#9A94BD`, primary `#B7A5FF`, error `#FF8FA3`, tertiary (live) `#7CE0C3`.
- Light: `#F6F4FD` / `#FFFFFF` / `#EFEBFC` / `#E1DBF7` / `#1E1A38` / `#625C88` / `#5B45D6` / `#C2364A`.
- Typography uses Sora throughout.

- [ ] **Step 1: Toolchain.** The controller installs the JDK and Android SDK before dispatch; the implementer only verifies. `java -version` must be ≥ 17. `ANDROID_HOME` must point to an SDK with `platform-tools`, `platforms;android-36` and matching `build-tools`. If anything is missing, report BLOCKED. Do not install system software.
- [ ] **Step 2: Write the failing test** `ProtocolJsonTest`:
  - It parses the README clip shape, including `"kind":"link"` and null `width`.
  - Unknown fields are ignored.
  - `parseWs` maps `clip.created` (with `live`), `clip.deleted`, `pong`, and any other type to `Unknown`. Malformed JSON gives `Unknown`, not an exception.
  - An `Instant` round-trips with an offset (`2026-10-28T10:00:00+00:00`) and with a `Z` suffix.
- [ ] **Step 3:** Run `cd android && ./gradlew testDebugUnitTest`. Expected: FAIL (does not compile). Then implement the scaffold and `Protocol.kt`.
- [ ] **Step 4:** Run `./gradlew testDebugUnitTest lintDebug assembleDebug`. Expected: BUILD SUCCESSFUL, and `app/build/outputs/apk/debug/app-debug.apk` exists.
- [ ] **Step 5: CI.** `android.yml` runs on pushes and PRs that touch `android/**`, on `ubuntu-latest`. It uses `actions/setup-java` (temurin 17+) and `android-actions/setup-android` is **not** used (the runner image has the SDK). It runs `./gradlew testDebugUnitTest lintDebug assembleDebug`. Task 9 adds the release job.
- [ ] **Step 6:** Commit `feat(android): project scaffold, Night vault theme, protocol models`.

### Task 3: Content hash, server URL rules, API client

**Files:**
- Create: `android/app/src/main/java/xyz/ficdev/privatepaste/core/ContentHash.kt`, `core/ServerUrl.kt`, `api/PrivatePasteApi.kt`, `api/ApiException.kt`
- Create: `android/app/src/main/java/xyz/ficdev/privatepaste/core/BitmapHashing.kt` (Android `Bitmap` → RGBA bytes → hash)
- Test: `android/app/src/test/java/xyz/ficdev/privatepaste/core/{ContentHashTest,ServerUrlTest,BitmapHashingTest}.kt`, `api/PrivatePasteApiTest.kt` (MockWebServer)
- Reference: `src/PrivatePaste.Protocol/ContentHash.cs`, `src/PrivatePaste.Client.Core/Api/PrivatePasteApi.cs`, `src/PrivatePaste.Client.Core/Settings/ServerUrl.cs`

**Interfaces:**
- Consumes: `Protocol.kt` (Task 2).
- Produces:
```kotlin
object ContentHash { fun text(text: String): String; fun pixels(rgba: ByteArray, width: Int, height: Int): String }
object BitmapHashing { fun hash(bitmap: Bitmap): String }   // getPixels() (unpremultiplied ARGB ints) → RGBA bytes
object ServerUrl { fun normalize(input: String): HttpUrl }  // throws IllegalArgumentException(message for the UI)
sealed class ApiException(message: String, cause: Throwable? = null) : Exception(message, cause) {
    class Unauthorized : ApiException("Unauthorized")                  // 401
    class Rejected(val status: Int, message: String) : ApiException(message)  // other 4xx (400, 404, 413, …)
    class RateLimited : ApiException("Too many attempts")              // 429 on login only
    class Transient(message: String, cause: Throwable? = null) : ApiException(message, cause) // IO, timeout, 5xx, 408, 429 (non-login), bad/unparseable 2xx body
}
class PrivatePasteApi(baseUrl: HttpUrl, private val token: String?, client: OkHttpClient) {
    suspend fun login(password: String, deviceName: String): LoginResponse   // platform "android"
    suspend fun logout()
    suspend fun uploadText(text: String, copiedAt: Instant?, backfill: Boolean): ClipDto
    suspend fun uploadImage(bytes: ByteArray, fileName: String, mime: String, copiedAt: Instant?, backfill: Boolean): ClipDto
    suspend fun clips(q: String?, cursor: String?, limit: Int = 50): ClipPage
    suspend fun restore(id: String): ClipDto?      // null on 404
    suspend fun delete(id: String): Boolean        // false on 404
    suspend fun sync(afterSeq: Long, limit: Int = 200): SyncPage
    suspend fun image(id: String): ByteArray
    suspend fun thumbnail(id: String): ByteArray
    fun webSocket(listener: WebSocketListener): WebSocket   // GET /ws with the bearer header
    companion object { fun uploadTimeout(bytes: Long): Duration /* 30 s + 1 s per started 256 KB, cap 5 min */ }
}
```
- Rules:
  - `ServerUrl` keeps a sub-path and returns it with a trailing `/`. Relative paths (`api/clips`, no leading slash) resolve against it.
  - Error messages:
    - "Enter a server address."
    - "Use an https:// address (http:// is only allowed for localhost)."
    - "That doesn't look like a server address."
  - Per-call timeouts use OkHttp `Call.timeout()`: 30 s by default, `uploadTimeout` for uploads, 15 s to connect the WebSocket. Coroutine cancellation cancels the call.

- [ ] **Step 1: Write the failing tests.**
  - `ContentHashTest`: the four README vectors verbatim, plus `a\r\nb` equals `a\nb` and a lone CR becomes LF.
  - `BitmapHashingTest` (Robolectric): a 1×1 bitmap `Color.argb(255,255,0,0)` gives `8d833d22…c49a`. A 2×1 semi-transparent pixel matches `ContentHash.pixels` on the unpremultiplied bytes.
  - `ServerUrlTest`:
    - `https://h` and `https://h/sub/` give `https://h/sub/`.
    - `http://localhost:5299` is OK.
    - `http://example.com` is rejected with the https message.
    - Blank input is rejected.
  - `PrivatePasteApiTest`:
    - The bearer header is on every call except login.
    - The login body is `{password, deviceName, platform:"android"}`.
    - Status mapping: 401 → `Unauthorized`; 413 → `Rejected(413)` carrying the problem `detail`; 503 → `Transient`; 429 → `RateLimited` on login and `Transient` elsewhere; an HTML 200 → `Transient`.
    - Multipart parts `file`, `copiedAt` (ISO) and `backfill=true` are present only when backfilling.
    - `sync` builds the query `afterSeq=7&limit=200`.
    - Restore and delete map 404 to null/false.
    - `uploadTimeout(1)` is 31 s and `uploadTimeout(100 MB)` is 5 min.
- [ ] **Step 2:** Run `./gradlew testDebugUnitTest`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `./gradlew testDebugUnitTest lintDebug`. Expected: PASS.
- [ ] **Step 5:** Commit `feat(android): content hash, server URL rules, API client`.

### Task 4: On-device storage and clipboard

**Files:**
- Create under `android/app/src/main/java/xyz/ficdev/privatepaste/storage/`: `KeyProvider.kt`, `CryptoFile.kt`, `SessionStore.kt`, `HistoryCache.kt`, `ThumbnailStore.kt`, `OutboundQueue.kt`, `SettingsStore.kt`
- Create under `.../clipboard/`: `ClipboardWriter.kt`, `ClipboardReader.kt`, `ClipImageFiles.kt`
- Create: `android/app/src/main/res/xml/file_paths.xml`; Modify: `AndroidManifest.xml` (a `FileProvider` with authority `xyz.ficdev.privatepaste.files` and `grantUriPermissions="true"`)
- Test: `android/app/src/test/java/xyz/ficdev/privatepaste/storage/*Test.kt`, `clipboard/ClipboardWriterTest.kt` (Robolectric)
- Reference: `src/PrivatePaste.Client.Core/Cache/HistoryCache.cs`, `Sync/OutboundQueue.cs`, `Cache/AtomicFile.cs`

**Interfaces:**
- Consumes: `Protocol.kt`.
- Produces:
```kotlin
interface KeyProvider { fun key(alias: String): SecretKey }   // KeystoreKeyProvider (AES-256-GCM, non-exportable); tests use an in-memory one
class CryptoFile(file: File, keys: KeyProvider, alias: String) { fun read(): ByteArray?; fun write(bytes: ByteArray) }
    // read(): null when missing OR undecryptable (logs the reason). write(): temp file + atomic rename
data class StoredSession(val serverUrl: String, val deviceId: String, val deviceName: String, val token: String)
class SessionStore(file: CryptoFile) { fun load(): StoredSession?; fun save(s: StoredSession); fun clear() }
class HistoryCache(file: CryptoFile, private val max: Int = 200) {
    val items: StateFlow<List<ClipDto>>          // lastCopiedAt desc
    val highestSeq: Long; val lastAppliedAt: Instant?; val lastAppliedHash: String?   // both persisted
    fun upsert(clip: ClipDto, fromSync: Boolean): Boolean   // false when nothing changed
    fun remove(id: String, seq: Long): Boolean
    fun markApplied(at: Instant, hash: String); fun advanceSeq(seq: Long); fun clear(); fun save()   // save is serialized, never concurrent
}
class ThumbnailStore(dir: File, keys: KeyProvider) { fun get(hash: String): ByteArray?; fun put(hash: String, png: ByteArray); fun prune(keep: Set<String>); fun clear() }
data class QueuedClip(val id: String /* uuid */, val kind: ClipKind, val text: String?, val imageFile: String?, val mime: String?,
    val copiedAt: Instant, val attempts: Int = 0)
class OutboundQueue(file: CryptoFile, imageDir: File, keys: KeyProvider) {
    fun enqueueText(text: String, copiedAt: Instant); fun enqueueImage(bytes: ByteArray, mime: String, copiedAt: Instant)
    fun peek(): QueuedClip?; fun imageBytes(c: QueuedClip): ByteArray?; fun removeHead()
    fun recordFailure(): Int /* new attempts */; fun moveHeadToBack(); val isEmpty: Boolean; val size: Int; fun clear()
}
enum class DeliveryMode { AUTOMATIC, NOTIFICATION }
class SettingsStore(context: Context) { var deliveryMode: DeliveryMode; var onboardingDone: Boolean }   // SharedPreferences, not secret
class ClipImageFiles(dir: File /* cacheDir/clipboard */) { fun store(clipId: String, png: ByteArray): File /* keeps newest 5 */ }
class ClipboardWriter(context: Context, files: ClipImageFiles) {
    fun writeText(text: String); fun writeImage(clipId: String, png: ByteArray)   // label "PrivatePaste"; image via FileProvider URI, MIME image/png
}
sealed interface ClipboardContent { data class Text(val text: String) : ClipboardContent; data class Image(val uri: Uri, val mime: String) : ClipboardContent }
class ClipboardReader(context: Context) { fun read(): ClipboardContent? }   // only works while an activity has focus; text wins over image
```
- HistoryCache rules (same as desktop):
  - `upsert` ignores a clip whose seq is ≤ the held seq for that id, and a clip whose tombstone seq is ≥ its seq. Tombstones keep the last 1000 ids and are persisted.
  - A same-hash bump keeps the existing thumbnail.
  - Only `fromSync = true` advances `highestSeq`, which is persisted separately from the items.
  - `markApplied` keeps the maximum.
  - The cache trims to `max` by `lastCopiedAt`.

- [ ] **Step 1: Write the failing tests.**
  - `CryptoFileTest`:
    - Data round-trips.
    - The file on disk does not contain the plaintext.
    - A tampered byte makes `read()` return null.
    - A key that disappears makes `read()` return null.
  - `SessionStoreTest.unreadableSessionLoadsAsSignedOut`.
  - `HistoryCacheTest`:
    - `corruptCacheStartsEmpty`.
    - Older-seq and tombstoned upserts are ignored (`upsert` returns false).
    - Own uploads (`fromSync=false`) don't move `highestSeq`.
    - The 201st item trims the oldest.
    - Save, then reload, restores `highestSeq`, `lastAppliedAt` and tombstones.
    - 50 concurrent `save()` calls from 4 threads never throw.
  - `OutboundQueueTest`:
    - The order is FIFO.
    - Image bytes are encrypted on disk.
    - `moveHeadToBack` keeps attempts.
    - The queue survives a reload.
    - `clear` deletes the image files.
  - `ClipboardWriterTest` (Robolectric):
    - Text lands on the primary clip.
    - An image lands as a `content://xyz.ficdev.privatepaste.files/...` URI with `image/png`.
    - Only the newest 5 image files are kept.
- [ ] **Step 2:** Run `./gradlew testDebugUnitTest`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `./gradlew testDebugUnitTest lintDebug`. Expected: PASS.
- [ ] **Step 5:** Commit `feat(android): encrypted session, history cache, queue, clipboard writer`.

### Task 5: Sync engine and session

**Files:**
- Create under `android/app/src/main/java/xyz/ficdev/privatepaste/sync/`: `Backoff.kt`, `SyncEngine.kt`, `WsLink.kt` (OkHttp WebSocket → `Flow<WsEvent>` + send), `LocalCaptureClock.kt`, `SessionManager.kt`, `ClipApplier.kt`
- Test: `android/app/src/test/java/xyz/ficdev/privatepaste/sync/{BackoffTest,SyncEngineTest,SessionManagerTest}.kt` (fake link and fake api via MockWebServer, or interfaces with `runTest` virtual time)
- Reference: `src/PrivatePaste.Client.Core/Sync/SyncEngine.cs`, `Backoff.cs`, and the `LastLocalCaptureAt` handling in `ClipboardCoordinator.cs`

**Interfaces:**
- Consumes: Task 3 `PrivatePasteApi`, `ApiException`; Task 4 `HistoryCache`, `OutboundQueue`, `SessionStore`, `ThumbnailStore`.
- Produces:
```kotlin
enum class SyncState { SIGNED_OUT, CONNECTING, CONNECTED, OFFLINE }
fun interface ClipApplier { suspend fun apply(clip: ClipDto): Boolean }   // true = written (or notification posted)
class LocalCaptureClock { val lastLocalCaptureAt: Instant?; fun record(at: Instant) }  // lock-free max
interface LinkFactory { suspend fun connect(): WsLink }   // throws on failure; 15 s timeout inside
interface WsLink { val events: Flow<WsEvent>; fun send(text: String): Boolean; fun close(code: Int, reason: String) }
    // events completes on close; a 1008 close completes with RevokedException
class SyncEngine(api: PrivatePasteApi, cache: HistoryCache, queue: OutboundQueue, thumbs: ThumbnailStore,
    applier: ClipApplier, flushQueue: suspend () -> Unit, links: LinkFactory, ownDeviceId: String,
    capture: LocalCaptureClock, clock: Clock /* java.time */, backoff: Backoff, scope: CoroutineScope) {
    val state: StateFlow<SyncState>; val revoked: Flow<Unit>   // emits once, then the engine is stopped
    fun start(); suspend fun stop(); fun nudge()               // nudge: drop the link / skip the backoff wait, reset backoff
}
sealed interface SignInResult { data object Ok; data object WrongPassword; data object RateLimited;
    data class InvalidUrl(val message: String); data class Unreachable(val message: String); data class Failed(val message: String) }
class SessionManager(store: SessionStore, cache: HistoryCache, thumbs: ThumbnailStore, queue: OutboundQueue,
    apiFactory: (baseUrl: HttpUrl, token: String?) -> PrivatePasteApi) {
    val session: StateFlow<StoredSession?>
    suspend fun signIn(url: String, password: String, deviceName: String): SignInResult  // clears cache, thumbs, queue on success
    suspend fun signOut()                 // best-effort logout call, then clear everything local
    suspend fun onRevoked()               // clear token, cache and queue; keep the server URL for the sign-in form
}
```
- Engine rules, per connection:
  1. Connect.
  2. Catch up via `sync(afterSeq = cache.highestSeq)` while `hasMore`, then `advanceSeq(latestSeq)`.
  3. Run `flushQueue()`.
  4. Receive events.
- Catch-up apply: apply at most one clip, the newest caught-up clip from another device. All of these must hold:
  - `lastAppliedAt != null` (so the first sync after sign-in is history only);
  - the clip is `cache.items[0]`;
  - it is newer than both `lastAppliedAt` and `capture.lastLocalCaptureAt`;
  - `queue.isEmpty`.
- Live events:
  - `ClipCreated(live=true)` from another device: upsert, then apply, then `markApplied(clip.lastCopiedAt, clip.hash)` when apply returned true.
  - `live=false`: history only.
  - Own device: upsert only.
  - `ClipDeleted`: `remove`.
- Image clips fetch their thumbnail in the background, at most 2 at a time, failures swallowed.
- Keepalive: send `{"type":"ping"}` every 25 s; drop the link if no pong arrives within 10 s.
- While connected with a non-empty queue, call `flushQueue()` every 30 s.
- Backoff is reset only after a link stayed up 25 s.
- A 401 from any call, or a 1008 close, emits `revoked` once and stops for good.
- Saves are throttled to at most every 2 s, plus one on `stop`.
- All waiting uses the injected clock and the test dispatcher.

- [ ] **Step 1: Write the failing tests.**
  - `BackoffTest`: 1, 2, 4 … capped at 30 s, within ±20%.
  - `SyncEngineTest`:
    - `catchUpAppliesOnlyNewest`
    - `firstSyncAfterSignInIsHistoryOnly`
    - `catchUpSkippedWhenQueueNotEmpty`
    - `catchUpSkippedWhenLocalSendIsNewer`
    - `liveClipFromOtherDeviceIsApplied`
    - `backfillAndOwnClipsAreNotApplied`
    - `deleteRemovesFromCache`
    - `outOfOrderBroadcastsKeepNewestSeq`
    - `close1008StopsForGood`
    - `http401StopsForGood`
    - `pongTimeoutReconnectsWithBackoff`
    - `nudgeReconnectsImmediately`
    - `periodicFlushWhileConnected`
    - `thumbnailFetchedForImageClip`
    - `thumbnailFailureIsSwallowed`
  - `SessionManagerTest`:
    - A wrong password gives `WrongPassword`, and 429 gives `RateLimited`.
    - `http://example.com` gives `InvalidUrl`.
    - A successful sign-in clears an old cache and queue, and saves the session.
    - `onRevoked` keeps `serverUrl` for the form.
- [ ] **Step 2:** Run `./gradlew testDebugUnitTest`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `./gradlew testDebugUnitTest lintDebug`. Expected: PASS, with no real sleeps in tests.
- [ ] **Step 5:** Commit `feat(android): sync engine and session manager`.

### Task 6: Foreground service, delivery, notifications, boot, network

**Files:**
- Create under `android/app/src/main/java/xyz/ficdev/privatepaste/`:
  - `PrivatePasteApp.kt` (the `Application`; owns `AppGraph`)
  - `AppGraph.kt` (wires the singletons from Tasks 3–5 and 7; rebuilt per session)
  - `service/SyncService.kt`
  - `service/Notifications.kt`
  - `service/ServiceClipApplier.kt`
  - `service/CopyActivity.kt` (transparent; writes the clip, then finishes)
  - `service/BootReceiver.kt`
  - `service/NetworkWatcher.kt`
  - `service/BatteryOptimization.kt`
- Modify: `AndroidManifest.xml` (service `foregroundServiceType="specialUse"` with `PROPERTY_SPECIAL_USE_FGS_SUBTYPE` = "Keeps a connection to the user's own clipboard server so copied text arrives instantly"; permissions `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_SPECIAL_USE`, `POST_NOTIFICATIONS`, `RECEIVE_BOOT_COMPLETED`, `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`, `INTERNET`, `ACCESS_NETWORK_STATE`; receiver for `BOOT_COMPLETED` and `MY_PACKAGE_REPLACED`)
- Test: `android/app/src/test/java/xyz/ficdev/privatepaste/service/*Test.kt` (Robolectric)

**Interfaces:**
- Consumes: Task 4 `ClipboardWriter`, `SettingsStore`; Task 5 `SyncEngine`, `SessionManager`, `ClipApplier`; Task 7 `ClipSender.flushQueue`.
- Produces (in addition): `PrivatePasteApp` implements Task 8's `UiDepsProvider`.
- Produces:
  - `SyncService.start(context)` / `SyncService.stop(context)`. The service runs only while signed in.
  - `Notifications` channels:
    - `connection`: IMPORTANCE_MIN, ongoing. Text "PrivatePaste connected", "Connecting…" or "Offline, retrying". Tapping it opens the history.
    - `clips`: IMPORTANCE_HIGH, heads-up. Title "New clip from {device}". Body: the first 100 chars, or "Image {w}×{h}". Action "Copy" opens `CopyActivity` with the clip id.
    - `alerts`: IMPORTANCE_DEFAULT. Used for signed out ("Signed out. Sign in again to keep syncing."), for "Couldn't sync a clip after several tries.", and for the size toasts when the app is in the background.
- `ServiceClipApplier` behaviour:
  - **AUTOMATIC:** `ClipboardWriter` writes the clip. Image clips are fetched first via `api.image`.
  - **NOTIFICATION:** posts the clip notification, which replaces the previous one (single notification id).
  - In both modes, a failed image fetch returns false.
- `CopyActivity`: writes the clip (from the cache, fetching the image if needed), shows the toast "Copied", then finishes. It runs in the foreground, so the write is always allowed.
- `NetworkWatcher`: `registerDefaultNetworkCallback`. `onAvailable` and capability changes call `engine.nudge()`, debounced 2 s.
- Engine `revoked` handling: `session.onRevoked()`, post the alert once, stop the service.
- `BootReceiver`: starts the service only if a session exists.
- `BatteryOptimization.isExempt(context)` / `requestIntent(context)` (`ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` for this package).

- [ ] **Step 1: Write the failing tests** (Robolectric):
  - `serviceStartsForegroundWithConnectionNotification`
  - `notificationTextFollowsState`
  - `automaticModeWritesClipboard`
  - `notificationModePostsCopyAction`
  - `copyActivityWritesAndFinishes`
  - `revokedStopsServiceAndNotifiesOnce`
  - `bootStartsServiceOnlyWhenSignedIn`
  - `networkCallbackNudgesEngineDebounced`
- [ ] **Step 2:** Run `./gradlew testDebugUnitTest`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `./gradlew testDebugUnitTest lintDebug assembleDebug`. Expected: PASS.
- [ ] **Step 5: Launch smoke without a device.** Robolectric starts `MainActivity`, then the service, against a MockWebServer that speaks the WS protocol. The state reaches CONNECTED.
- [ ] **Step 6:** Commit `feat(android): foreground sync service, delivery modes, boot and network`.

### Task 7: Sending — share target, Quick Settings tile, offline queue

**Files:**
- Create under `android/app/src/main/java/xyz/ficdev/privatepaste/send/`: `ClipSender.kt`, `ShareActivity.kt`, `SendClipboardTileService.kt`, `ClipboardSendActivity.kt` (transparent)
- Modify: `AndroidManifest.xml`:
  - `ShareActivity` intent filters: `ACTION_SEND` `text/plain`, `ACTION_SEND` `image/*`. Its theme is translucent with no history.
  - The tile service: `BIND_QUICK_SETTINGS_TILE`, label "Send clipboard", icon `ic_notification`.
- Test: `android/app/src/test/java/xyz/ficdev/privatepaste/send/*Test.kt`
- Reference: the upload/queue/flush parts of `src/PrivatePaste.Client.Core/Sync/ClipboardCoordinator.cs`

**Interfaces:**
- Consumes: Task 3 `PrivatePasteApi`, `ContentHash`, `BitmapHashing`; Task 4 `OutboundQueue`, `HistoryCache`, `ClipboardReader`; Task 5 `LocalCaptureClock`.
- Produces:
```kotlin
sealed interface SendResult { data object Sent; data object Queued; data object AlreadySynced;
    data class TooLarge(val message: String); data class Rejected(val message: String); data object SignedOut }
class ClipSender(api: PrivatePasteApi, queue: OutboundQueue, cache: HistoryCache, capture: LocalCaptureClock,
    notifier: (String) -> Unit, clock: Clock) {
    suspend fun sendText(text: String): SendResult
    suspend fun sendImage(open: () -> InputStream, mime: String): SendResult   // reads at most 20 MB + 1 byte
    suspend fun flushQueue()                                                   // passed to SyncEngine
}
```
- Send rules:
  - Size checks come before anything else.
  - If the content hash equals `cache.lastAppliedHash`, return `AlreadySynced` ("Already on your other devices").
  - Live upload (`backfill=false`):
    - On success, upsert with `fromSync=false` and `capture.record(serverClip.lastCopiedAt)`. Toast "Sent to your devices".
    - On `Transient`: enqueue with the copy time, `capture.record(copyTime)`, toast "Offline. Will send when connected."
    - On `Rejected`: show its message.
    - On `Unauthorized`: `SignedOut`.
- `flushQueue` uploads in FIFO order as `backfill=true` with the original `copiedAt`:
  - Transient failure: `recordFailure()`. At 5 attempts the item goes to the back; at 10 it is dropped with a single notifier message, "Couldn't sync a clip after several tries."
  - `Rejected`: drop the item with its message.
  - `Unauthorized`: rethrow, so the engine treats it as revoked.
  - The function returns while paused or cancelled without counting an attempt.
- `ShareActivity` sends the shared item, toasts the result and finishes. Multiple items (`ACTION_SEND_MULTIPLE`) are not registered.
- The tile's `onClick`:
  - Use `startActivityAndCollapse(PendingIntent)` on API 34+, and the `Intent` overload below that.
  - Open `ClipboardSendActivity`. It reads the clipboard in `onWindowFocusChanged(true)`, sends, toasts and finishes.
  - An empty clipboard gives "Clipboard is empty".

- [ ] **Step 1: Write the failing tests.**
  - `sendTextLiveSuccessRecordsCapture`
  - `transientFailureQueuesAndToastsOffline`
  - `queuedSendsFlushInOrderAsBackfill`
  - `poisonItemRotatesThenDrops`
  - `rejectedShowsServerMessage`
  - `oversizeTextRefused`
  - `oversizeImageIsRefusedWithoutReadingWholeStream` (an InputStream that counts bytes read, ≤ 20 MB + 1)
  - `alreadySyncedSkipsUpload`
  - `shareActivitySendsTextAndFinishes` (Robolectric)
  - `clipboardSendActivityReadsOnFocus` (Robolectric)
- [ ] **Step 2:** Run `./gradlew testDebugUnitTest`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `./gradlew testDebugUnitTest lintDebug`. Expected: PASS.
- [ ] **Step 5:** Commit `feat(android): share target, Quick Settings tile, offline send queue`.

### Task 8: UI — sign-in, history, settings

**Files:**
- Create under `android/app/src/main/java/xyz/ficdev/privatepaste/ui/`: `AppNav.kt`, `signin/{SignInScreen,SignInViewModel}.kt`, `history/{HistoryScreen,HistoryViewModel,ClipRow,ThumbnailLoader}.kt`, `settings/{SettingsScreen,SettingsViewModel}.kt`, `onboarding/PermissionsStep.kt`, `RelativeTime.kt`
- Modify: `MainActivity.kt`
- Test: `android/app/src/test/java/xyz/ficdev/privatepaste/ui/*Test.kt` (ViewModels on the JVM; Compose screens rendered with Robolectric + `createComposeRule`)

**Interfaces:**
- Produces: `interface UiDeps` (exposes everything below that the screens need: session manager, cache, thumbnail store, clipboard writer, settings, `api(): PrivatePasteApi?`, `syncState: StateFlow<SyncState>`, `startService: () -> Unit`, battery helpers as lambdas) and `interface UiDepsProvider { val uiDeps: UiDeps }`. `MainActivity` reads `(application as UiDepsProvider).uiDeps`. Task 6's `PrivatePasteApp` implements `UiDepsProvider`. ViewModels take their dependencies as constructor parameters, and tests use fakes.
- Consumes: Task 5 `SessionManager` (`session`, `signIn`, `signOut`), `SyncEngine.state`; Task 4 `HistoryCache.items`, `ThumbnailStore`, `ClipboardWriter`, `SettingsStore`; Task 3 `PrivatePasteApi` (`clips`, `restore`, `delete`, `thumbnail`, `image`); Task 6 `BatteryOptimization`, `SyncService.start` (call through a lambda so this task compiles before Task 6 lands).
- Screens and copy:
  - **Sign in.** Logo plus the fields "Server address", "Password" and "Device name" (default `Build.MODEL`), and the button "Sign in". Errors:
    - "Wrong password."
    - "Too many attempts. Wait a minute and try again."
    - The ServerUrl message.
    - "Can't reach the server. Check the address and your connection."
  - **Onboarding**, once, after the first sign-in:
    1. On 33+, ask for the notification permission ("PrivatePaste shows a quiet notification while it's connected").
    2. Show "Allow PrivatePaste to run in the background" with the button "Allow" (battery exemption) and the link "Not now".
  - **History.**
    - A top search field with the placeholder "Search history".
    - The list comes from the cache first; a query of 2+ chars, debounced 300 ms, also asks the server, and the results are merged with cached hits first and deduped by id. Older pages load via `nextCursor` at the end of the list.
    - Row: type icon (text, link, image), a one-line preview or a 48 dp thumbnail, then "{device} · {relative time}". The newest row gets a `#7CE0C3` dot when it was applied by this phone.
    - Tap: copy locally first (always works in the foreground), then `restore(id)` in the background. Toast "Copied". When offline, only the local copy happens (images need the cached image or show "Image not available offline").
    - Long-press: the dialog "Delete this clip from all your devices?" with "Delete" and "Cancel". Then `delete(id)` and remove the row.
    - Empty state: "Nothing here yet. Copy something on your computer and it shows up here."
    - Status chip in the app bar from `SyncState`: "Connected", "Connecting…", "Offline".
    - Settings icon.
  - **Settings.**
    - Server (read-only) and device name (read-only).
    - "Delivery": the radio options "Copy automatically" and "Show a notification with a Copy button".
    - "Background": the status "Allowed" or "Restricted", with the button "Allow" when restricted.
    - "Sign out", with the confirm text "Sign out of this phone? Local history is removed."
    - Version.
- Relative time: "just now" (<1 min), "{n} min", "{n} h", "yesterday", then `d MMM`.

- [ ] **Step 1: Write the failing tests.**
  - `SignInViewModelTest`: each error message, and a disabled button while busy.
  - `HistoryViewModelTest`:
    - A cache-only list arrives with no network call.
    - Cached hits are merged first and deduped.
    - Paging appends.
    - Tap copies before restore.
    - Tap offline copies only.
    - Delete removes the row.
  - `RelativeTimeTest`.
  - `HistoryScreenTest` (Robolectric Compose): empty state text; a long-press shows the delete dialog.
- [ ] **Step 2:** Run `./gradlew testDebugUnitTest`. Expected: FAIL.
- [ ] **Step 3:** Implement. Thumbnails come from `ThumbnailStore` first, then `api.thumbnail`, with an LRU of 100 in memory.
- [ ] **Step 4:** Run `./gradlew testDebugUnitTest lintDebug assembleDebug`. Expected: PASS. Save Robolectric screenshots of the three screens (dark and light) to `android/app/build/screenshots/` when `PP_SCREENSHOT_DIR` is set.
- [ ] **Step 5:** Commit `feat(android): sign-in, history and settings screens`.

### Task 9: Release signing, CI release, README, end-to-end check

**Files:**
- Modify: `android/app/build.gradle.kts` (a `release` signing config read from the env vars `PP_ANDROID_KEYSTORE` (path), `PP_ANDROID_KEYSTORE_PASSWORD`, `PP_ANDROID_KEY_ALIAS`, `PP_ANDROID_KEY_PASSWORD`; when they are absent, the release build is unsigned and the build says so; R8 on, with keep rules for kotlinx.serialization)
- Modify: `.github/workflows/android.yml` (job `release` on tag `android-v*`: decode the secret `ANDROID_KEYSTORE_BASE64` to a file, run `assembleRelease`, rename the output to `PrivatePaste-android-<version>.apk`, create the release with `gh release create`, and mark it a prerelease when the version after `android-v` contains `-`)
- Create: `scripts/android-keystore.ps1` (generates `android/keystore/release.jks` with `keytool`, prints the `gh secret set` commands for the owner to run, and warns: "Back this file up. Without it, updates can't be installed over the existing app.")
- Modify: `README.md` (new "Android app" section)
- Create: `android/app/src/test/java/xyz/ficdev/privatepaste/e2e/LocalServerE2eTest.kt` (runs only when `PP_E2E_URL` is set)

- [ ] **Step 1: Write the opt-in e2e test** against a LOCAL server (`PP_E2E_URL=http://localhost:5299`, password `dev`):
  1. Sign in as "e2e-phone".
  2. A second api client signs in as "e2e-pc" and uploads text.
  3. The engine applies it through a recording `ClipApplier` within 2 s.
  4. `ClipSender.sendText` from the phone appears in `GET /api/clips` from the pc client.
  5. Delete propagates.
  6. Revoking the phone from the pc client emits `revoked`.
- [ ] **Step 2:** Start the local server per README "Local development" with a temp data dir. Run `PP_E2E_URL=http://localhost:5299 ./gradlew testDebugUnitTest --tests '*LocalServerE2eTest*'`. Expected: PASS. Without the variable, the test is skipped.
- [ ] **Step 3:** Signing config, workflow and keystore script. Verify with `./gradlew assembleRelease` (it must succeed unsigned with no env vars) and with a throwaway keystore generated in a temp dir (it must produce a signed APK; check with `apksigner verify`).
- [ ] **Step 4: README "Android app" section.**
  - Download the APK from Releases and allow "Install unknown apps" for the browser or file manager.
  - Sign in.
  - Allow notifications and background use.
  - Add the "Send clipboard" tile (pull down Quick Settings, edit, drag it in).
  - Share to PrivatePaste from any app.
  - The delivery setting, and when to switch to Notification mode (if clips don't land on the clipboard while the phone is locked or in another app).
  - OEM battery notes: Xiaomi, Samsung, Huawei. Point to the phone's own "battery → unrestricted" setting.
  - Updating: install a newer APK over the old one; it needs the same signing key.
  - Release: `scripts/android-keystore.ps1`, the four secrets, then tag `android-v0.1.0`.
- [ ] **Step 5:** Run the full `./gradlew testDebugUnitTest lintDebug assembleDebug assembleRelease` plus `dotnet test`. Expected: all pass.
- [ ] **Step 6:** Commit `feat(android): release signing, CI release, README`.

---

## Owner checklist (on the phone; agents cannot do these)

1. Install the debug APK (from `android/app/build/outputs/apk/debug/`, or a CI artifact). Sign in to https://paste.ficdev.xyz with the real password.
2. **Spike, spec §7 risk 1:** with the screen off for 10 min, copy on the PC, then unlock. Is it on the clipboard? Repeat with another app open, and again after a few hours or overnight. If it fails, switch Delivery to "Show a notification" and repeat.
3. Share a camera photo to PrivatePaste. It must appear upright on the PC.
4. The Quick Settings tile sends the current clipboard. Sharing from Chrome sends the link.
5. History: search, tap to copy (paste in another app), long-press to delete (it disappears on the PC).
6. Revoke the phone in the web UI. The phone shows "Signed out…" and stops.
7. Reboot. The service comes back by itself.
8. Generate the release keystore, add the GitHub secrets, tag `android-v0.1.0`, and install the release APK over the debug one. That fails, because the signing key differs: uninstall first, once.
