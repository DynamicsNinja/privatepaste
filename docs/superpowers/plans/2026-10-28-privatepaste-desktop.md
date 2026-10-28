# PrivatePaste Desktop App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the PrivatePaste tray app for Windows (first-class), with macOS and Linux as best-effort. It watches the clipboard, syncs with the live server, applies clips arriving from other devices, and has a Ctrl+Shift+V popup to search history and paste.

**Architecture:** A UI-free `PrivatePaste.Client.Core` library holds everything testable:
- the API client
- the WebSocket sync engine
- the encrypted local cache and offline queue
- echo prevention
- the clipboard coordinator

It talks to the OS only through small interfaces. `PrivatePaste.Desktop` is the Avalonia app: tray, windows, Night vault theme. It also contains the per-OS implementations of those interfaces (clipboard, secret storage, autostart) and the global hotkey and paste injection, built on SharpHook. Core is tested against the real server in-process (`WebApplicationFactory`), including real WebSockets.

**Tech Stack:** .NET 10; Avalonia (latest stable, pin the exact version) with CommunityToolkit.Mvvm; SharpHook for the global hotkey and key simulation; SkiaSharp for image decode and encode (same as the server); `System.Security.Cryptography.ProtectedData` (DPAPI) on Windows; xUnit.

**Spec:** `docs/superpowers/specs/2026-10-28-privatepaste-design.md` (§6.2 desktop, §6.4 auth, §6.5 sync rules). The server's actual API contract is the **API section of `README.md`**, which is binding where it is more specific than the spec. Branding is in `branding/README.md`.

**Scope:** Plan 2 of 3. Windows gets full behaviour and an installable build. macOS and Linux get the platform services and a build that compiles and publishes; runtime verification on those systems is done by the owner. The Android app is plan 3.

## Global Constraints

- Target `net10.0` (Core and tests) and `net10.0` for the Avalonia app, using RuntimeIdentifiers for win-x64, osx-arm64, osx-x64 and linux-x64. Nullable on, warnings as errors (inherited from `Directory.Build.props`).
- Server URL must be `https://`. Plain `http://` is allowed only for `localhost`, `127.0.0.1` and `[::1]`.
- Default hotkey **Ctrl+Shift+V** (Cmd+Shift+V on macOS), configurable.
- Autostart is **on by default**.
- The local cache keeps the most recent **500** items (text and thumbnails), encrypted at rest with OS facilities.
- Size limits: text **1 MB**, images **20 MB**. Larger items are not uploaded; show a notification instead.
- **Pause sync** stops sending and receiving. Clips copied while paused are never uploaded, not even after Resume.
- Echo prevention: a clip the app itself writes to the clipboard must never be uploaded back.
- Content hashes must match the README test vectors exactly. Use `PrivatePaste.Protocol.ContentHash`; never reimplement it.
- Live clips from other devices replace the clipboard. Backfilled clips, and clips whose `sourceDeviceId` is this device, never do.
- **Picking an item in the popup re-syncs it** (owner decision): call `POST /api/clips/{id}/restore`, then write the item to the clipboard and paste.
- Reconnect with exponential backoff: 1 s doubling to a 30 s cap, with ±20% jitter. Never reconnect after a `1008 revoked` close; show the user as signed out instead.
- Branding: Night vault tokens from `branding/README.md`, dark by default, Sora font bundled from `src/PrivatePaste.Server/wwwroot/fonts`, app icon from `branding/app-icon-512.png`.
- No third-party network services. The app talks only to the configured server.
- Unsigned builds (owner decision). The README documents the SmartScreen "Run anyway" step and the macOS quarantine step.

**Deviations from the spec (Claude's calls, recorded):**
- **Windows clipboard watching:** polls `GetClipboardSequenceNumber` every **250 ms** instead of using `AddClipboardFormatListener`. It costs one integer read per tick, needs no hidden window plumbing, and stays well inside the 2 s latency budget.
- **Windows token storage:** a DPAPI-protected file (`CurrentUser` scope) instead of Credential Manager. It gives the same protection with one code path for the token and the cache key.

## Review Focus

1. **Echo loop.** When a clip from another device is written to the clipboard, the watcher sees that change. It must not upload the clip back. Test in Task 5.
2. **Copying while the server is unreachable.** The copy is queued and uploaded later in copy order with `backfill=true`, and must never overwrite another device's clipboard. A restart in between must not lose the queue. Test in Task 5.
3. **Oversized clipboard content.** A 5 MB text or a 40 MB screenshot is skipped with one notification. Nothing crashes and nothing is retried forever. Test in Task 5.
4. **Revoked device or logout from the web UI.** The app stops reconnecting, clears its token and cache, and shows the signed-out state. It must not spin in a retry loop. Test in Task 6.
5. **Clipboard locked by another app (Windows).** `OpenClipboard` failing is retried a few times, then skipped for that change. It is never fatal and never blocks the UI thread. Test in Task 8.

---

### Task 1: Client core scaffold and settings

**Files:**
- Create: `src/PrivatePaste.Client.Core/PrivatePaste.Client.Core.csproj` (references `PrivatePaste.Protocol`)
- Create: `src/PrivatePaste.Client.Core/Settings/ClientSettings.cs`, `src/PrivatePaste.Client.Core/Settings/SettingsStore.cs`, `src/PrivatePaste.Client.Core/Settings/ServerUrl.cs`
- Modify: `PrivatePaste.slnx`, `tests/PrivatePaste.Tests/PrivatePaste.Tests.csproj` (reference Client.Core)
- Test: `tests/PrivatePaste.Tests/Client/SettingsTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record ClientSettings(
    string? ServerUrl = null, string DeviceName = "", string Hotkey = "Ctrl+Shift+V",
    bool Autostart = true, bool Paused = false, Guid? DeviceId = null);
public static class ServerUrl {
    // Trims, removes trailing '/', requires https (http only for localhost/127.0.0.1/[::1]). Throws ArgumentException with a user-facing message.
    public static Uri Normalize(string input);
}
public sealed class SettingsStore(string directory) {   // file: {directory}/settings.json, ProtocolJson options
    public ClientSettings Load();                      // missing or corrupt file → defaults (corrupt file renamed to settings.json.bad)
    public void Save(ClientSettings settings);         // atomic: write .tmp then File.Move(overwrite)
}
public static class AppPaths { public static string DataDirectory { get; } }   // %APPDATA%/PrivatePaste, ~/Library/Application Support/PrivatePaste, $XDG_CONFIG_HOME/privatepaste
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory, InlineData("https://paste.ficdev.xyz/", "https://paste.ficdev.xyz/"), InlineData(" https://x.io/sub/ ", "https://x.io/sub"),
 InlineData("http://localhost:5299", "http://localhost:5299/"), InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
void Normalizes(string input, string expected) => Assert.Equal(expected, ServerUrl.Normalize(input).ToString());
[Theory, InlineData("http://paste.ficdev.xyz"), InlineData("ftp://x.io"), InlineData("paste.ficdev.xyz"), InlineData("")]
void Rejects(string input) => Assert.Throws<ArgumentException>(() => ServerUrl.Normalize(input));
[Fact] void Defaults_when_missing() { var s = new SettingsStore(TempDir()).Load(); Assert.True(s.Autostart); Assert.Equal("Ctrl+Shift+V", s.Hotkey); Assert.False(s.Paused); }
[Fact] void Roundtrip() { /* save then load equal */ }
[Fact] void Corrupt_file_yields_defaults_and_is_kept_aside() { /* write "{not json", Load → defaults, settings.json.bad exists */ }
```

- [ ] **Step 2: Run `dotnet test --filter SettingsTests`** and expect FAIL (compile).
- [ ] **Step 3: Implement** the types above. `Normalize` uses `Uri.TryCreate` with an absolute URI, the scheme check, and the loopback host check.
- [ ] **Step 4: Run the tests again** and expect PASS.
- [ ] **Step 5: Commit** with `feat(client): core library, settings and server URL validation`

---

### Task 2: Secret protection and token store

**Files:**
- Create: `src/PrivatePaste.Client.Core/Security/ISecretProtector.cs`, `DpapiSecretProtector.cs`, `SecretFile.cs`
- Test: `tests/PrivatePaste.Tests/Client/SecretFileTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface ISecretProtector { byte[] Protect(byte[] plaintext); byte[] Unprotect(byte[] protectedData); }
[SupportedOSPlatform("windows")] public sealed class DpapiSecretProtector : ISecretProtector   // ProtectedData, DataProtectionScope.CurrentUser, fixed entropy "PrivatePaste.v1"
public sealed class SecretFile(string path, ISecretProtector protector) {
    public string? Read();        // null if missing or unprotect fails (corrupt or other user) — never throws
    public void Write(string value);  // atomic tmp+move
    public void Delete();
}
```

The token is stored at `{DataDirectory}/token.bin`. The cache key (Task 4) is a random 32-byte key stored base64 at `{DataDirectory}/cache.key`; both use a `SecretFile`.

- [ ] **Step 1: Write the failing tests** using a `FakeProtector` that XORs with a constant and also verifies a header, so Unprotect of foreign data throws.

```csharp
[Fact] void Roundtrip() { f.Write("tok-123"); Assert.Equal("tok-123", f.Read()); }
[Fact] void File_is_not_plaintext() => Assert.DoesNotContain("tok-123", File.ReadAllText(path));
[Fact] void Missing_returns_null() => Assert.Null(new SecretFile(Missing(), fake).Read());
[Fact] void Garbage_returns_null() { File.WriteAllBytes(path, [1,2,3]); Assert.Null(f.Read()); }
[Fact, Trait("os","windows")] void Dpapi_roundtrip() { if (!OperatingSystem.IsWindows()) return; /* Protect/Unprotect real DPAPI */ }
```

- [ ] **Step 2: Run the tests** and expect FAIL. **Step 3: Implement.** **Step 4: Run the tests** and expect PASS.
- [ ] **Step 5: Commit** with `feat(client): protected secret files (DPAPI on Windows)`

---

### Task 3: API client

**Files:**
- Create: `src/PrivatePaste.Client.Core/Api/PrivatePasteApi.cs`, `src/PrivatePaste.Client.Core/Api/ApiException.cs`
- Test: `tests/PrivatePaste.Tests/Client/ApiClientTests.cs`

**Interfaces:**
- Consumes: Protocol DTOs, `ProtocolJson.Options`.
- Produces:

```csharp
public class ApiException(HttpStatusCode status, string message) : Exception(message) { public HttpStatusCode Status { get; } = status; }
public sealed class UnauthorizedApiException(string m) : ApiException(HttpStatusCode.Unauthorized, m);   // token revoked or wrong password
public sealed class PrivatePasteApi(HttpClient http) {         // http.BaseAddress = server URL; caller owns HttpClient
    public string? Token { get; set; }                          // sent as Bearer when set
    public Task<LoginResponse> LoginAsync(string password, string deviceName, string platform, CancellationToken ct = default);
    public Task LogoutAsync(CancellationToken ct = default);
    public Task<ClipDto> UploadTextAsync(string text, DateTimeOffset? copiedAt, bool backfill, CancellationToken ct = default);
    public Task<ClipDto> UploadImageAsync(byte[] png, DateTimeOffset? copiedAt, bool backfill, CancellationToken ct = default); // multipart: file (image/png, "clip.png"), copiedAt (ISO "O"), backfill ("true"/"false")
    public Task<ClipPage> ListAsync(string? query, string? cursor, int limit = 50, CancellationToken ct = default);
    public Task<ClipDto> RestoreAsync(Guid id, CancellationToken ct = default);
    public Task DeleteAsync(Guid id, CancellationToken ct = default);
    public Task<byte[]> GetImageAsync(Guid id, CancellationToken ct = default);
    public Task<byte[]> GetThumbnailAsync(Guid id, CancellationToken ct = default);
    public Task<SyncResponse> SyncAsync(long afterSeq, int limit = 200, CancellationToken ct = default);
}
```

Error mapping: 401 → `UnauthorizedApiException`. Any other non-success → `ApiException`, with the message taken from the problem-details `detail` or `title` when present, otherwise `"{status} {reason}"`. Network failures surface as `HttpRequestException` (Task 5 uses that to decide "offline").

- [ ] **Step 1: Write the failing tests.** These are integration tests against an in-process server. Use `using var server = new PrivatePasteFactory(); var api = new PrivatePasteApi(server.CreateClient());`.

```csharp
[Fact] Login_then_upload_and_list()             // LoginAsync(PrivatePasteFactory.Password,"PC-test","windows"); Token = r.Token; UploadTextAsync("hello",null,false); ListAsync(null,null) → Items[0].Text=="hello"
[Fact] Wrong_password_throws_unauthorized()
[Fact] Revoked_token_throws_unauthorized()       // login A and B; B deletes A via HTTP; A.ListAsync → UnauthorizedApiException
[Fact] Image_upload_roundtrip()                  // PNG from SkiaSharp 10x10 → UploadImageAsync → Kind==Image, Width==10; GetThumbnailAsync non-empty PNG
[Fact] Backfill_keeps_copied_at()                // set server FakeTimeProvider clock T; copiedAt T-1h backfill:true → LastCopiedAt == T-1h
[Fact] Too_large_text_is_api_exception_413()     // factory with PP_MAX_TEXT_KB=1; 2000 chars → ApiException.Status == 413
[Fact] Sync_returns_changes_after_seq()
[Fact] Restore_bumps_and_delete_removes()
[Fact] Logout_revokes_token()
```

- [ ] **Step 2: Run `dotnet test --filter ApiClientTests`** and expect FAIL. **Step 3: Implement.** **Step 4: Run the tests** and expect PASS.
- [ ] **Step 5: Commit** with `feat(client): typed API client`

---

### Task 4: Encrypted history cache

**Files:**
- Create: `src/PrivatePaste.Client.Core/Cache/HistoryCache.cs`
- Test: `tests/PrivatePaste.Tests/Client/HistoryCacheTests.cs`

**Interfaces:**
- Consumes: `ISecretProtector` only to obtain the cache key through `SecretFile`. The cache itself encrypts with AES-256-GCM using that key: nonce(12) ‖ tag(16) ‖ ciphertext, the same layout as the server's `BlobStore`.
- Produces:

```csharp
public sealed class HistoryCache(string path, byte[] key, int capacity = 500) {
    public long HighestSeq { get; }                        // highest seq ever seen (clips and deletions)
    public DateTimeOffset? LastAppliedAt { get; }           // LastCopiedAt of the last clip applied to the clipboard
    public IReadOnlyList<CachedClip> Items { get; }         // newest first by LastCopiedAt desc, then Seq desc
    public void Upsert(ClipDto clip, byte[]? thumbnailPng = null);   // replaces by Id; trims to capacity
    public void Remove(Guid id, long seq);
    public void MarkApplied(ClipDto clip);
    public IReadOnlyList<CachedClip> Search(string query);  // case-insensitive (OrdinalIgnoreCase) substring over Text; images never match a non-empty query
    public void Save();                                     // atomic tmp+move, encrypted; called by owners after a batch of changes
    public static HistoryCache Load(string path, byte[] key, int capacity = 500);  // missing, corrupt or wrong-key → empty cache (never throws)
    public void Clear();                                    // used on sign-out; deletes the file
}
public sealed record CachedClip(ClipDto Clip, byte[]? ThumbnailPng);
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Orders_newest_first()
[Fact] Upsert_same_id_replaces_and_reorders()       // bump: new LastCopiedAt moves it to top, no duplicate
[Fact] Trims_to_capacity()                          // capacity 3, insert 5 → 3 newest remain
[Fact] Remove_deletes_and_raises_highest_seq()
[Fact] Search_is_case_insensitive_and_skips_images()  // "DOCKER" finds "docker compose"; "šećer" finds "kupiti šećer"
[Fact] Persists_encrypted()                         // Save, raw file does not contain the text "supersecret"; Load with same key → same items, HighestSeq, LastAppliedAt
[Fact] Wrong_key_or_corrupt_loads_empty()
```

- [ ] **Step 2: Run the tests** and expect FAIL. **Step 3: Implement.** **Step 4: Run the tests** and expect PASS.
- [ ] **Step 5: Commit** with `feat(client): encrypted local history cache`

---

### Task 5: Echo guard, outbound queue and clipboard coordinator

**Files:**
- Create: `src/PrivatePaste.Client.Core/Platform/ClipboardContent.cs`, `IClipboardAccess.cs`, `INotifier.cs`
- Create: `src/PrivatePaste.Client.Core/Sync/EchoGuard.cs`, `OutboundQueue.cs`, `ClipboardCoordinator.cs`
- Create: `src/PrivatePaste.Client.Core/Images/ImageHashing.cs`
- Modify: Client.Core csproj (SkiaSharp, the same version as the server)
- Test: `tests/PrivatePaste.Tests/Client/ClipboardCoordinatorTests.cs`, `OutboundQueueTests.cs`, `EchoGuardTests.cs`

**Interfaces:**
- Produces:

```csharp
public abstract record ClipboardContent { public sealed record Text(string Value) : ClipboardContent; public sealed record Image(byte[] Png) : ClipboardContent; }
public interface IClipboardAccess {
    event EventHandler? Changed;                                // raised on any clipboard change (from the platform watcher)
    Task<ClipboardContent?> ReadAsync(CancellationToken ct);    // null when the clipboard holds nothing supported
    Task WriteAsync(ClipboardContent content, CancellationToken ct);
}
public interface INotifier { void Notify(string message); }    // short, user-facing, one line
public static class ImageHashing { public static string HashPng(byte[] png); }   // decode with SkiaSharp to Rgba8888/Unpremul → ContentHash.ForPixels; throws InvalidDataException on undecodable data
public sealed class EchoGuard(TimeProvider time, TimeSpan window) {   // window = 30 s
    public void Remember(string hash);         // called right before the app writes the clipboard
    public bool IsEcho(string hash);           // true (and consumed) if remembered within window
}
public sealed class OutboundQueue(string path, byte[] key) {       // encrypted like HistoryCache; persisted on every change
    public int Count { get; }
    public void Enqueue(ClipboardContent content, DateTimeOffset copiedAt);
    public bool TryPeek(out (ClipboardContent Content, DateTimeOffset CopiedAt) item);
    public void Dequeue();
    public static OutboundQueue Load(string path, byte[] key);        // corrupt → empty
}
public sealed class ClipboardCoordinator(
    IClipboardAccess clipboard, PrivatePasteApi api, HistoryCache cache, OutboundQueue queue,
    EchoGuard echo, INotifier notifier, TimeProvider time, Func<bool> isPaused) {
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxImageBytes = 20 * 1024 * 1024;
    public Task OnClipboardChangedAsync(CancellationToken ct);  // read → paused? drop : echo? drop : too big? notify+drop : upload live, else (HttpRequestException/timeout) enqueue
    public Task FlushQueueAsync(CancellationToken ct);           // uploads queued items in order with backfill:true; stops at first network failure; drops items the server rejects with 4xx (notify once per item)
    public Task ApplyRemoteAsync(ClipDto clip, CancellationToken ct);  // fetch content (text in DTO, image via GetImageAsync) → echo.Remember(clip.Hash) → clipboard.WriteAsync → cache.MarkApplied
}
```

Rules:
- On a successful live upload, `cache.Upsert(result)`.
- Paused means `OnClipboardChangedAsync` returns without reading.
- Empty or whitespace-only text is ignored silently, because the server would reject it with 400.
- The size check uses UTF-8 byte length for text and PNG byte length for images.

- [ ] **Step 1: Write the failing tests.** Use a `FakeClipboard : IClipboardAccess` that records writes and lets the test set the current content, a `ListNotifier`, `FakeTimeProvider`, and a real in-process server (`PrivatePasteFactory`) for the API.

```csharp
[Fact] Copy_uploads_live_and_caches()
[Fact] Remote_clip_written_to_clipboard_is_not_uploaded_back()   // ApplyRemoteAsync(clip from device B) then OnClipboardChangedAsync → server list still has exactly 1 item, its SourceDeviceName == "B"
[Fact] Echo_window_expires()                                       // Remember, advance 31 s, same content copied by the user → uploaded
[Fact] Paused_copy_is_never_uploaded_even_after_resume()            // paused=true, change, paused=false, FlushQueueAsync → server empty, queue empty
[Fact] Offline_copy_is_queued_then_flushed_in_order_with_backfill() // api with HttpClient whose handler throws HttpRequestException for 2 copies (T0 "a", T1 "b"); swap to real; FlushQueueAsync → server has a@T0 and b@T1, both backfill (LastCopiedAt == copiedAt)
[Fact] Queue_survives_restart()                                     // enqueue, new OutboundQueue.Load(...) → Count==1, same content
[Fact] Oversized_text_is_skipped_with_one_notification()            // 1_048_577 ASCII chars → no upload, notifier has exactly 1 message mentioning "too large"
[Fact] Oversized_image_is_skipped_with_one_notification()
[Fact] Whitespace_only_is_ignored()
[Fact] Image_hash_matches_protocol_vector()                         // 1x1 PNG of RGBA [255,0,0,255] → "8d833d22068fdf91eecbd0d310208548d1f9418c6513c572a7c7d01fc696c49a"
```

- [ ] **Step 2: Run the tests** and expect FAIL. **Step 3: Implement.** **Step 4: Run the tests** and expect PASS.
- [ ] **Step 5: Commit** with `feat(client): clipboard coordinator with echo guard and offline queue`

---

### Task 6: Sync engine (WebSocket, catch-up, reconnect)

**Files:**
- Create: `src/PrivatePaste.Client.Core/Sync/SyncEngine.cs`, `src/PrivatePaste.Client.Core/Sync/Backoff.cs`
- Test: `tests/PrivatePaste.Tests/Client/SyncEngineTests.cs`, `BackoffTests.cs`

**Interfaces:**
- Consumes: `PrivatePasteApi`, `HistoryCache`, `ClipboardCoordinator`, `ProtocolJson`, `WsMessage`.
- Produces:

```csharp
public enum SyncState { SignedOut, Connecting, Connected, Offline, Paused }
public sealed class Backoff(Random random) { public TimeSpan Next(); public void Reset(); }   // 1s,2s,4s…30s cap, ±20% jitter
public sealed class SyncEngine(
    PrivatePasteApi api, HistoryCache cache, ClipboardCoordinator coordinator,
    Func<CancellationToken, Task<WebSocket>> connect, Guid ownDeviceId,
    TimeProvider time, Backoff backoff, Func<bool> isPaused) : IAsyncDisposable {
    public SyncState State { get; }
    public event EventHandler<SyncState>? StateChanged;
    public event EventHandler? HistoryChanged;           // cache changed (UI refresh)
    public event EventHandler? Revoked;                  // 1008 close or 401 → host signs out
    public void Start();                                  // runs the loop on a background task
    public Task StopAsync();
}
```

Loop per connection:
1. Connect.
2. Catch up with `SyncAsync(cache.HighestSeq)` until `!HasMore`. Upsert clips and remove `DeletedIds` in the cache. Then pick the newest caught-up clip that is from another device (by `LastCopiedAt`). If it is newer than `cache.LastAppliedAt` and not paused, call `ApplyRemoteAsync` once.
3. Call `coordinator.FlushQueueAsync`.
4. Set `State = Connected` and `backoff.Reset()`.
5. Receive loop:
   - `clip.created` from another device: upsert in the cache. If `Live == true` and not paused, call `ApplyRemoteAsync`.
   - `clip.created` from this device: upsert in the cache only.
   - `clip.deleted`: remove from the cache.
   - Raise `HistoryChanged` after each message, and `cache.Save()` at most every 2 s.
6. App-level `ping` every 25 s. If no `pong` arrives within 10 s, abort and reconnect.

On close or failure: a `1008` close, or a `401` from any API call, raises `Revoked` and stops for good. Anything else sets `State = Offline` and reconnects after `backoff.Next()`. While paused, the engine stays disconnected (`State = Paused`) and resumes when `isPaused()` turns false; it polls every 1 s.

- [ ] **Step 1: Write the failing tests.** Run them against an in-process server, with `connect = ct => factory.Server.CreateWebSocketClient()` plus the bearer header `ConnectAsync(new Uri(factory.Server.BaseAddress, "ws"), ct)`. Each wait uses a 5 s timeout helper (`WaitUntilAsync(() => cond)`), and there are no sleeps.

```csharp
[Fact] Live_clip_from_other_device_is_applied()         // engine for A; B uploads "hi" → FakeClipboard got "hi"; cache has it
[Fact] Own_clip_is_cached_not_applied()
[Fact] Backfill_clip_is_cached_not_applied()
[Fact] Delete_removes_from_cache()
[Fact] Catch_up_after_offline_applies_only_newest_once() // B uploads x,y,z while A stopped; A starts → clipboard writes == ["z"]; cache has x,y,z
[Fact] Catch_up_does_not_reapply_already_applied()       // restart A with same cache → zero writes
[Fact] Revoked_raises_event_and_stops_reconnecting()     // B revokes A → Revoked raised once; State==SignedOut; no further connect calls within 3 backoff periods (count connect invocations)
[Fact] Server_drop_reconnects_with_backoff()             // connect factory that fails twice then succeeds → State reaches Connected; backoff delays observed via FakeTimeProvider
[Fact] Paused_engine_disconnects_and_ignores_updates()
// BackoffTests: sequence within ±20% of 1,2,4,8,16,30,30; Reset → 1s again
```

- [ ] **Step 2: Run the tests** and expect FAIL. **Step 3: Implement.** **Step 4: Run the tests** and expect PASS. Run the WebSocket tests 3 times and expect no flakes.
- [ ] **Step 5: Commit** with `feat(client): sync engine with catch-up, live apply and reconnect`

---

### Task 7: Desktop app shell (Avalonia, Night vault theme, tray, sign-in, settings, autostart)

**Files:**
- Create: `src/PrivatePaste.Desktop/PrivatePaste.Desktop.csproj` (WinExe, Avalonia, CommunityToolkit.Mvvm, references Client.Core; `ApplicationIcon` from `Assets/app.ico`)
- Create: `src/PrivatePaste.Desktop/Program.cs`, `App.axaml(.cs)`, `Theme/NightVault.axaml` (resources: the brand tokens; dark default, light variant via `ThemeVariant`), `Assets/` (copy `branding/logo.svg` and `branding/app-icon-512.png`; generate `app.ico` with 16/32/48/256 sizes from the PNG; add the Sora variable TTF from the google/fonts `ofl/sora` directory plus its `OFL.txt`, because Avalonia loads TTF/OTF, not WOFF2)
- Create: `src/PrivatePaste.Desktop/DesktopHost.cs`: composition root that owns settings, secrets, API, cache, queue, coordinator, engine and platform services, plus sign-in and sign-out
- Create: `src/PrivatePaste.Desktop/Tray/TrayController.cs`, `Views/SignInWindow.axaml(.cs)`, `Views/SettingsWindow.axaml(.cs)`, `Views/ToastWindow.axaml(.cs)`, `ViewModels/SignInViewModel.cs`, `ViewModels/SettingsViewModel.cs`
- Create: `src/PrivatePaste.Desktop/Platform/IAutostart.cs`, `Platform/Windows/WindowsAutostart.cs` (HKCU `Software\Microsoft\Windows\CurrentVersion\Run`, value `PrivatePaste` = quoted exe path + ` --minimized`), `Platform/PlatformServices.cs` (selects implementations per OS)
- Test: `tests/PrivatePaste.Tests/Client/SignInViewModelTests.cs`, `SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces:
  - `public enum SignInResult { Ok, WrongPassword, InvalidUrl, Unreachable, RateLimited }` and `public sealed record SignInOutcome(SignInResult Result, string? Message)` (`Message` carries the `ServerUrl.Normalize` text for `InvalidUrl`).
  - `DesktopHost` with `Task<SignInOutcome> SignInAsync(string serverUrl, string password, string deviceName)` (normalizes the URL, logs in with `platform = "windows" | "macos" | "linux"`, stores the token, starts the engine), `Task SignOutAsync()` (best-effort logout, deletes the token, clears the cache and queue), `SyncState State`, `bool Paused { get; set; }` (persisted), `event EventHandler? HistoryChanged`, `HistoryCache Cache`, `PrivatePasteApi Api`, and `ClipboardCoordinator Coordinator`.
  - `INotifier` implementation `ToastNotifier`: a small borderless window at the bottom-right of the primary screen, shown for 4 s, at most one at a time (a new message replaces the old).
  - `IAutostart { bool IsEnabled { get; } void Set(bool enabled); }`.
- **Tray menu** (in this order):
  - Status line, disabled. It reads one of "Connected to paste.ficdev.xyz", "Offline, retrying", "Paused" or "Signed out".
  - Open history (Ctrl+Shift+V)
  - Pause sync / Resume sync
  - Settings…
  - Sign out
  - Quit
- **Tray icon:** the app icon normally, and the icon at 50% opacity while paused or offline.
- **Sign-in window:**
  - Fields: Server URL (prefilled `https://`), Password, Device name (defaults to the machine name).
  - Primary button "Sign in". On failure it shows the exact message: "Wrong password.", the `ServerUrl.Normalize` message, "Can't reach the server.", or "Too many attempts. Wait a minute and try again." for 429.
- **Settings window:**
  - Fields: server (read-only, shown with the device name), hotkey (text box validated by `HotkeyGesture.TryParse` in Task 9), "Start with Windows" (or "Start at login" on macOS and Linux), and "Sign out".
- **Startup:** `--minimized`, or any start after first run, launches straight to the tray. If no token is stored, the sign-in window opens instead.
- **Single instance:** a named mutex `PrivatePaste.SingleInstance.{username}`. A second launch exits immediately.

- [ ] **Step 1: Write the failing view-model tests** (plain C#, no UI thread). `SignInViewModel(Func<string,string,string,Task<SignInOutcome>>)` exposes `Error`, `IsBusy`, and `SignInCommand.CanExecute` (false while busy or a field is empty). Assert each error message above for the corresponding `SignInResult`. `SettingsViewModel` has a `Hotkey` property and `HotkeyError` ("Use a combination like Ctrl+Shift+V."), and applies a valid hotkey through a callback.
- [ ] **Step 2: Run the tests** and expect FAIL. **Step 3: Implement** the view models, then the views and theme, the tray, the host and autostart. **Step 4: Run the tests** and expect PASS.
- [ ] **Step 5: Manual check** with `dotnet run --project src/PrivatePaste.Desktop`:
  - Sign in to a local server (`PP_PASSWORD=dev`, `http://localhost:5299`). The tray shows "Connected".
  - Pause, then Resume.
  - Sign out. The sign-in window opens.
  - Take screenshots of the sign-in window, settings and tray menu in dark mode for the report.
- [ ] **Step 6: Commit** with `feat(desktop): Avalonia shell with tray, sign-in, settings and autostart`

---

### Task 8: Windows clipboard access

**Files:**
- Create: `src/PrivatePaste.Desktop/Platform/Windows/WindowsClipboard.cs` (`IClipboardAccess`), `Platform/Windows/Win32Clipboard.cs` (P/Invoke: `GetClipboardSequenceNumber`, `OpenClipboard`, `CloseClipboard`, `EmptyClipboard`, `GetClipboardData`, `SetClipboardData`, `RegisterClipboardFormat("PNG")`, `IsClipboardFormatAvailable`, `GlobalAlloc/Lock/Unlock`), `Platform/Windows/DibConverter.cs`
- Create: `src/PrivatePaste.Desktop/Platform/ClipboardRetry.cs` (OS-agnostic retry policy, unit-tested)
- Test: `tests/PrivatePaste.Tests/Client/ClipboardRetryTests.cs`, `DibConverterTests.cs` (Windows-only facts skip elsewhere)

**Interfaces:**
- Produces: `WindowsClipboard : IClipboardAccess, IDisposable` and `ClipboardRetry.RunAsync<T>(Func<T> attempt, int attempts = 5, TimeSpan delay = 30 ms)`. The retry returns `default` after the last failed attempt, never throws for `ClipboardBusyException`, and logs once.
- **Watching:** a background timer polls `GetClipboardSequenceNumber` every **250 ms** and raises `Changed` when it differs. This runs on a dedicated thread, never the UI thread.
- **Read priority:**
  1. Registered `"PNG"` format, as bytes.
  2. `CF_DIBV5` or `CF_DIB`, converted to PNG by `DibConverter.ToPng(byte[] dib)` with SkiaSharp, handling BI_RGB 24/32-bit and BI_BITFIELDS, bottom-up and top-down.
  3. `CF_UNICODETEXT`.
  4. Otherwise `null`.
- **Write:**
  - Text: `CF_UNICODETEXT`, with LF converted to CRLF so Windows apps paste line breaks correctly. The hash is unaffected because hashing normalizes line endings.
  - Image: both `"PNG"` and `CF_DIB`, made by `DibConverter.FromPng`, so Paint, Office and browsers all accept it.
- **`OpenClipboard` failure** (another app holds it): throw `ClipboardBusyException` internally. All public operations go through `ClipboardRetry`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Retry_returns_value_after_transient_failures()     // throws Busy 2x then returns 42 → 42
[Fact] Retry_gives_up_quietly()                           // always Busy → default, no exception, attempt count == 5
[Fact] Dib_roundtrip_preserves_pixels()                   // PNG 3x2 known pixels → FromPng → ToPng → decoded pixels equal (Windows-agnostic: pure byte conversion)
[Fact] Bottom_up_and_top_down_dib_both_decode()
[Fact] Windows_text_roundtrip()                           // returns early unless OperatingSystem.IsWindows(); Write Text("a\nb") → Read → Text("a\r\nb"); ContentHash equal to ForText("a\nb")
[Fact] Windows_change_event_fires()                       // returns early unless Windows; subscribe, write text, Changed within 1 s
```

- [ ] **Step 2: Run the tests** and expect FAIL. **Step 3: Implement.** **Step 4: Run the tests** and expect PASS.
- [ ] **Step 5: Wire it** into `PlatformServices` for Windows. In `DesktopHost`, `Changed` calls `Coordinator.OnClipboardChangedAsync`, serialized so at most one runs at a time and later changes coalesce.
- [ ] **Step 6: Manual check:**
  - Copy text and a Snipping Tool screenshot on the PC. Both appear in the web UI at paste.ficdev.xyz within 2 s.
  - Upload a clip from a second device (curl or the web UI's Copy). It lands on the PC clipboard and is not echoed back: still one entry, and its source is unchanged.
- [ ] **Step 7: Commit** with `feat(desktop): Windows clipboard watcher, reader and writer`

---

### Task 9: History popup, global hotkey and paste injection

**Files:**
- Create: `src/PrivatePaste.Desktop/ViewModels/PopupViewModel.cs`, `Views/PopupWindow.axaml(.cs)`
- Create: `src/PrivatePaste.Desktop/Input/HotkeyGesture.cs`, `Input/HotkeyService.cs` (SharpHook `TaskPoolGlobalHook`), `Input/PasteInjector.cs` (SharpHook `EventSimulator`; Windows also restores the previous foreground window via `GetForegroundWindow`/`SetForegroundWindow` captured at hotkey time)
- Test: `tests/PrivatePaste.Tests/Client/PopupViewModelTests.cs`, `HotkeyGestureTests.cs`

**Interfaces:**
- Consumes: `HistoryCache`, `PrivatePasteApi`, `ClipboardCoordinator`, `EchoGuard` (through the coordinator).
- Produces:

```csharp
public sealed record HotkeyGesture(bool Ctrl, bool Shift, bool Alt, bool Meta, KeyCode Key) {   // SharpHook KeyCode
    public static bool TryParse(string text, out HotkeyGesture? gesture);   // "Ctrl+Shift+V", "Cmd+Shift+V", case-insensitive; needs ≥1 modifier and exactly one non-modifier key
    public override string ToString();                                      // canonical "Ctrl+Shift+V"
}
public sealed partial class PopupViewModel : ObservableObject {
    public PopupViewModel(HistoryCache cache, Func<string, CancellationToken, Task<IReadOnlyList<ClipDto>>> serverSearch, Func<ClipDto, Task> activate, TimeProvider time);
    [ObservableProperty] string query;                 // typing filters cached items immediately; server search debounced 250 ms, results merged after cached hits, de-duplicated by Id
    public ObservableCollection<PopupItem> Items { get; }
    [ObservableProperty] int selectedIndex;            // 0 after every Items reset
    public void MoveSelection(int delta);              // clamps, never wraps
    public Task ActivateSelectedAsync();               // calls activate(selected clip); no-op when empty
}
public sealed record PopupItem(ClipDto Clip, string Preview, string Meta, byte[]? Thumbnail);   // Preview: first line of text, ≤ 120 chars, or "Image W×H"; Meta: "{SourceDeviceName}, {relative time}"
```

- **Activate:**
  1. Hide the popup.
  2. `RestoreAsync(id)`, which re-syncs to the other devices per the owner decision. If the server is unreachable, use the cached copy without the restore and continue.
  3. `coordinator.ApplyRemoteAsync(restoredClip)`, which writes the clipboard and remembers the echo.
  4. Refocus the previous window.
  5. After 60 ms, simulate Ctrl+V (Cmd+V on macOS).
- **Popup window:**
  - Borderless, topmost, 420×480.
  - Positioned at the mouse cursor and clamped to that screen's working area.
  - Night vault styling identical to the web history rows (newest row highlighted only when the query is empty).
  - Search box focused on open.
  - Keys: ↑/↓ moves the selection, Enter activates, Esc closes, and losing focus closes.
  - Mouse click activates.
- **Paste on macOS:** if Accessibility permission is missing (SharpHook reports a failure), fall back to copy-only. Show the one-time toast "Copied. Allow PrivatePaste in Accessibility settings to paste automatically."

- [ ] **Step 1: Write the failing tests**

```csharp
// HotkeyGestureTests
[Theory, InlineData("Ctrl+Shift+V"), InlineData("ctrl+shift+v"), InlineData("Cmd+Shift+V"), InlineData("Alt+F2")] Parses(string s)
[Theory, InlineData("V"), InlineData("Ctrl+Shift"), InlineData("Ctrl+V+B"), InlineData("")] Rejects(string s)
[Fact] Canonical_to_string()                      // "shift+ctrl+v" → "Ctrl+Shift+V"
// PopupViewModelTests (cache pre-filled, fake server search, FakeTimeProvider)
[Fact] Empty_query_shows_cache_newest_first()
[Fact] Typing_filters_cache_immediately()
[Fact] Server_results_merge_after_cached_without_duplicates()   // advance 250 ms → server results appended, Id dedup
[Fact] Fast_typing_debounces_server_calls()                     // 4 keystrokes within 200 ms → 1 server call with the final query
[Fact] Selection_clamps_and_resets()
[Fact] Activate_calls_activate_with_selected_clip()
[Fact] Preview_and_meta_format()                                // multi-line text → first line; image → "Image 1280×720"; meta "PC-work, 2 min ago"
```

- [ ] **Step 2: Run the tests** and expect FAIL. **Step 3: Implement** the view model, gesture parsing, hotkey service, injector and window, and wire them into `DesktopHost` and the tray's "Open history". **Step 4: Run the tests** and expect PASS.
- [ ] **Step 5: Manual check (Windows):**
  - In Notepad, press Ctrl+Shift+V. The popup opens at the cursor.
  - Type to filter, press ↓ then Enter. The item is pasted into Notepad, and it moves to the top of the web UI.
  - Esc closes the popup without changes.
  - Change the hotkey in Settings. The new combination works and the old one stops working.
- [ ] **Step 6: Commit** with `feat(desktop): history popup with global hotkey and paste`

---

### Task 10: Windows packaging and end-to-end check

**Files:**
- Create: `scripts/publish-windows.ps1` — `dotnet publish src/PrivatePaste.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64`, then zip to `artifacts/PrivatePaste-win-x64.zip`
- Create: `.github/workflows/desktop.yml` — on tag `desktop-v*`: test + publish windows (and Task 11's mac/linux) + attach zips to a GitHub Release
- Modify: `README.md` — "Desktop app" section: download, SmartScreen "More info → Run anyway", first sign-in, hotkey, pause, where data lives (`%APPDATA%\PrivatePaste`), uninstall (quit, untick Start with Windows, delete the folder)
- Modify: `.gitignore` (`artifacts/`)

- [ ] **Step 1: Run `pwsh scripts/publish-windows.ps1`** and expect a single `PrivatePaste.exe` plus the zip, with no build warnings.
- [ ] **Step 2: End-to-end check against the live server** (https://paste.ficdev.xyz):
  - Run the published exe and sign in as device `PC-test`.
  - Copy text, a link and a screenshot. Each shows in the web UI within 2 s.
  - Restore a clip from the web UI. It lands on the PC clipboard.
  - Use the hotkey popup to paste into Notepad.
  - Quit and start the app again. Startup is minimized, it stays signed in, and the history appears instantly from the cache.
  - Revoke `PC-test` in the web UI. The app shows "Signed out" within a few seconds and stops retrying.
  - Record results and screenshots in the task report.
- [ ] **Step 3: Commit** with `chore(desktop): Windows publish script, release workflow, README`

---

### Task 11: macOS and Linux platform services (best-effort)

**Files:**
- Create: `src/PrivatePaste.Desktop/Platform/Polling/AvaloniaPollingClipboard.cs`. This `IClipboardAccess` uses Avalonia's clipboard and polls every 500 ms. It compares text, plus the PNG bytes for `image/png` when the platform exposes that format; otherwise it is text-only, documented.
- Create: `Platform/Mac/KeychainSecretProtector.cs` (`ISecretProtector`). It keeps a random 32-byte key in the login keychain via `security add-generic-password -s PrivatePaste -a data-key -w <base64> -U` and reads it with `find-generic-password -w`, then AES-GCM protects with it. `Platform/Mac/MacAutostart.cs` writes `~/Library/LaunchAgents/xyz.ficdev.privatepaste.plist`.
- Create: `Platform/Linux/SecretToolProtector.cs`, which does the same through `secret-tool store/lookup --label=PrivatePaste service privatepaste key data`. If `secret-tool` is missing, it falls back to a key file with mode 600 and shows a one-time toast saying so. `Platform/Linux/XdgAutostart.cs` writes `~/.config/autostart/privatepaste.desktop`.
- Modify: `Platform/PlatformServices.cs` to select these on macOS and Linux. Wayland is detected via `WAYLAND_DISPLAY`: on Wayland, show a one-time toast "Clipboard sync on Wayland is limited; X11 sessions work best.".
- Create: `scripts/publish-macos.sh` (osx-arm64 and osx-x64, `.app` bundle with Info.plist, bundle id `xyz.ficdev.privatepaste`, icon `.icns` made from `app-icon-512.png` via `iconutil` in CI) and `scripts/publish-linux.sh` (linux-x64 tar.gz plus the `.desktop` file and icon)
- Modify: `.github/workflows/desktop.yml` with macOS and Linux jobs (runners `macos-latest`, `ubuntu-latest`); `README.md` with mac (right-click → Open, or `xattr -d com.apple.quarantine`; grant Accessibility; re-grant after updates if lost) and Linux (X11 recommended, `secret-tool` from libsecret-tools) notes
- Test: `tests/PrivatePaste.Tests/Client/AutostartFileTests.cs` (the generated plist and `.desktop` file content, OS-agnostic string tests)

- [ ] **Step 1: Write the failing tests** for the plist and `.desktop` content: exec path quoted, `--minimized` present, and `X-GNOME-Autostart-enabled=true`.
- [ ] **Step 2: Run the tests** and expect FAIL. **Step 3: Implement.** **Step 4: Run the tests** and expect PASS.
- [ ] **Step 5: Run `dotnet publish src/PrivatePaste.Desktop -c Release -r osx-arm64` and `-r linux-x64`** on this Windows machine. Expect both to succeed. Runtime verification on real macOS and Linux is the owner's step; list it in the report.
- [ ] **Step 6: Commit** with `feat(desktop): macOS and Linux platform services and packaging (best-effort)`
