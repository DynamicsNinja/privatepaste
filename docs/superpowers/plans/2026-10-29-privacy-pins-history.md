# Private copies, pinned clips and history clearing: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Copies that password managers mark as private (or that come from blocked apps) never leave the desktop. Any clip can be pinned and shows in a Pinned tab on every client. History can be cleared, or auto-deleted after an age set in the web UI, without ever touching pins.

**Architecture:** The server stores `pinned_at`/`unpinned_at` on clips plus a small `settings` table. Pin changes are ordinary clip updates (new `seq`, broadcast as `clip.created` with `live: false`, so every client, old or new, treats them as history-only). Clears and auto-deletes go through the existing delete path, with tombstones. The desktop decides "private" when it reads the clipboard and never hands such content to the coordinator's upload path. Clients keep pinned clips beyond their cache limit.

**Tech Stack:** .NET 10 / ASP.NET Core minimal APIs / SQLite3 Multiple Ciphers / Razor Pages; Avalonia 12 + SharpHook (desktop); Kotlin + Compose + kotlinx.serialization + Robolectric (Android); xUnit.

**Spec:** `docs/superpowers/specs/2026-10-29-privacy-pins-history-design.md`. Read it; it is the authority where this plan is silent.

## Global Constraints

- Protocol stays compatible both ways with desktop 0.1.5 and Android 0.1.2: new fields optional, nothing renamed or removed; Android fields that can be missing are nullable with a default.
- A pin change never reaches a clipboard and never changes a clip's `lastCopiedAt` or its place in History.
- Pinned clips are never deleted by Clear history or auto-delete.
- A private copy reaches no server, log line with content, cache file or other device. Log lines may only say "Skipped a copy marked private." / "Skipped a copy from a blocked app." Never log clip text, sizes of private copies, typed keys or window titles.
- Auto-delete choices are exactly off, 7, 30, 90 days; off by default. A clip's age = later of `last_copied_at` and `unpinned_at`.
- Backfilled uploads copied before the last clear, or older than the auto-delete age, are rejected with **410 Gone** (problem title `"Cleared"`); live uploads are never rejected for age.
- The Windows popup keeps never taking the focus (`PopupWindow.KeepsFocusBelow`); nothing in it becomes focusable.
- UI copy and look follow `DESIGN.md`; plain sentence-case copy. Strings fixed here: "Pinned", "History", "Pin", "Unpin", "Clear history", "Auto-delete history", "Off", "After 7 days", "After 30 days", "After 90 days", "Private copies", "Ignore copies from these apps".
- The schema migration is idempotent and transactional (pattern: `PrivatePasteDb.Migrate`). Pushing to `main` migrates the live server: the owner backs up `/data` first.
- `TreatWarningsAsErrors` is on.

## Review Focus

1. **A private copy made after a remote clip arrived while offline** must still win: when the device reconnects, catch-up must not overwrite the clipboard with the older remote clip. Skipping a private copy still records the local capture time (test in Task 5).
2. **Pinning the newest clip on device A while device B is offline**: when B catches up, its clipboard is not rewritten (the clip's `lastCopiedAt` is unchanged). Test in Task 2 at server level (pin keeps `lastCopiedAt`) and Task 5 (catch-up after a pin applies nothing).
3. **Clear history with thousands of clips**: one request, all tombstoned, blobs removed, other devices catch up with every deletion. Test with 1,000 clips in Task 1.
4. **Unpin, then auto-delete runs**: an old clip unpinned now survives the next hourly check. Test in Task 3.
5. **Pinned clips beyond the cache limit** (desktop 500, Android 200) stay in the client cache and its Pinned tab after trimming. Tests in Tasks 5 and 9.

---

### Task 1: Server storage — pins, settings, bulk deletes

**Files:**
- Modify: `src/PrivatePaste.Server/Storage/Schema.sql`, `src/PrivatePaste.Server/Storage/PrivatePasteDb.cs` (Migrate), `src/PrivatePaste.Server/Storage/ClipRepository.cs`, `src/PrivatePaste.Protocol/` (ClipDto)
- Create: `src/PrivatePaste.Server/Storage/SettingsRepository.cs`
- Test: `tests/PrivatePaste.Tests/Storage/PinsAndClearTests.cs`, `tests/PrivatePaste.Tests/Storage/SettingsRepositoryTests.cs`

**Interfaces:**
- Produces:
  - `ClipDto` gains `public DateTimeOffset? PinnedAt { get; init; }` (JSON `pinnedAt`, omitted when null like other optional fields).
  - Columns `clips.pinned_at TEXT NULL`, `clips.unpinned_at TEXT NULL`; table `settings(name TEXT PRIMARY KEY, value TEXT NOT NULL)`; index `ix_clips_pinned ON clips(pinned_at DESC, seq DESC) WHERE pinned_at IS NOT NULL`.
  - `ClipRepository.SetPinnedAsync(Guid id, bool pinned, DateTimeOffset at) -> Task<ClipDto?>`: null if missing; no change (same seq returned) when already in that state; otherwise new seq, `pinned_at` = at or null, unpin sets `unpinned_at` = at. Never touches `last_copied_at`.
  - `ClipRepository.ListPinnedAsync(string? query, string? cursor, int limit) -> Task<ClipPage>`: pinned only, newest pinned first, same cursor/limit rules as `ListAsync`.
  - `ClipRepository.DeleteUnpinnedAsync(DateTimeOffset? olderThan) -> Task<IReadOnlyList<(ClipDto Clip, long Seq)>>`: deletes, in one transaction with tombstones, every unpinned clip (or only those whose max(`last_copied_at`, `unpinned_at`) < olderThan); returns what was deleted with each tombstone seq.
  - `SettingsRepository(PrivatePasteDb db)`: `GetAutoDeleteDaysAsync() -> Task<int?>`, `SetAutoDeleteDaysAsync(int? days)` (throws `ArgumentOutOfRangeException` unless null/7/30/90), `GetHistoryClearedAtAsync() -> Task<DateTimeOffset?>`, `SetHistoryClearedAtAsync(DateTimeOffset at)`.

- [ ] **Step 1: Write the failing tests**: `Pin_sets_pinnedAt_and_a_new_seq_but_keeps_lastCopiedAt`, `Pinning_twice_changes_nothing`, `Unpin_clears_pinnedAt_and_records_unpinnedAt`, `ListPinned_is_newest_pinned_first_and_searchable`, `DeleteUnpinned_keeps_pins_and_tombstones_the_rest` (1,000 clips, 3 pinned; afterwards `SyncAsync(0)` lists 3 clips and 997 deleted ids), `DeleteUnpinned_olderThan_uses_the_later_of_copy_and_unpin`, `Migration_adds_columns_to_an_existing_database_once` (open a DB created by the current schema minus the new columns, run Initialize twice), settings round-trip and `SetAutoDeleteDays(14)` throws.
- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~PinsAndClear|FullyQualifiedName~SettingsRepository"`: fails to compile.
- [ ] **Step 3: Implement** the schema, migration step (pattern of the `source_device_platform` step: `HasColumn` check, one transaction), the repository methods, `SettingsRepository` (register it in `Program.cs` like `ClipRepository`). The delete returns clips so the caller can remove blobs and broadcast.
- [ ] **Step 4: Run** the same filter, then all of `dotnet test`: pass.
- [ ] **Step 5: Commit** `feat(server): pinned clips, settings and bulk deletes in storage`.

### Task 2: Server service and API

**Files:**
- Modify: `src/PrivatePaste.Server/Clips/ClipService.cs`, `src/PrivatePaste.Server/Api/ClipEndpoints.cs`, `docs/api.md`
- Create: `src/PrivatePaste.Server/Api/SettingsEndpoints.cs` (mapped in `Program.cs`)
- Test: `tests/PrivatePaste.Tests/Api/PinEndpointsTests.cs`, `tests/PrivatePaste.Tests/Api/HistoryEndpointsTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces (all bearer- or cookie-authenticated like the existing clip endpoints; cookie POST/PUT/DELETE need the antiforgery header):
  - `POST /api/clips/{id}/pin` and `DELETE /api/clips/{id}/pin` → 200 `ClipDto`, 404 if missing. Broadcast `ClipChangedAsync(clip, live: false)` only when it changed.
  - `GET /api/clips?pinned=true&q=&cursor=&limit=` → `ClipPage` of pinned clips (the existing endpoint gains the `pinned` flag).
  - `POST /api/history/clear` → 200 `{ "deleted": n }`; deletes unpinned clips via `ClipService.ClearAsync()`, removes their blobs, records `history_cleared_at` = now, broadcasts each `ClipDeletedAsync`.
  - `GET /api/settings` → `{ "autoDeleteDays": null|7|30|90 }`; `PUT /api/settings` same body → 200, 400 for any other value.
  - Backfill uploads (`backfill: true` text or image) whose effective `copiedAt` is before `history_cleared_at`, or older than now − autoDeleteDays, → 410 problem with title `Cleared`, nothing stored.
  - `ClipService.ClearAsync() -> Task<int>` and `ClipService.DeleteOlderThanAsync(DateTimeOffset cutoff) -> Task<int>` (shared delete-blobs-and-broadcast code with `DeleteAsync`).

- [ ] **Step 1: Write the failing tests** (pattern: existing `tests/PrivatePaste.Tests/Api` tests with the test server): pin/unpin status codes and broadcast (`live: false`, no broadcast on a no-op), `GET clips?pinned=true` order, pin keeps `lastCopiedAt` and position in `GET clips`, clear returns the count and keeps pins, clear then a backfill copied before it → 410, a live upload after a clear → 200, settings PUT 30 then GET, PUT 14 → 400, backfill older than auto-delete age → 410.
- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~PinEndpoints|FullyQualifiedName~HistoryEndpoints"`: fails.
- [ ] **Step 3: Implement**, and document the endpoints, `pinnedAt` and the 410 in `docs/api.md` (client rules: a pin change arrives as `clip.created` with `live: false`; treat 410 on a queued upload as "drop quietly").
- [ ] **Step 4: Run** all tests: pass.
- [ ] **Step 5: Commit** `feat(server): pin, clear-history and settings endpoints`.

### Task 3: Server auto-delete

**Files:**
- Create: `src/PrivatePaste.Server/Clips/AutoDeleteService.cs` (a `BackgroundService`, registered in `Program.cs`)
- Test: `tests/PrivatePaste.Tests/Clips/AutoDeleteServiceTests.cs`

**Interfaces:**
- Consumes: `SettingsRepository.GetAutoDeleteDaysAsync`, `ClipService.DeleteOlderThanAsync`.
- Produces: `AutoDeleteService(SettingsRepository settings, ClipService clips, TimeProvider time, ILogger<AutoDeleteService> log)` with `internal Task<int> RunOnceAsync()`; runs once at start, then every hour (`TimeSpan.FromHours(1)`), via the injected `TimeProvider`. Logs "Auto-delete removed N clips." when N > 0.

- [ ] **Step 1: Write the failing tests** with `FakeTimeProvider`: off → deletes nothing; 30 days → deletes an unpinned clip copied 31 days ago, keeps one copied 29 days ago, keeps a pinned one copied a year ago; **a clip copied a year ago and unpinned now survives** (Review Focus 4); advancing the fake clock one hour triggers a second run.
- [ ] **Step 2–4:** run (fail), implement, run all tests (pass).
- [ ] **Step 5: Commit** `feat(server): hourly auto-delete of old unpinned clips`.

### Task 4: Web UI — Pinned tab, pin controls, settings page

**Files:**
- Modify: `src/PrivatePaste.Server/Pages/Index.cshtml(.cs)`, `src/PrivatePaste.Server/Pages/Shared/_Layout.cshtml` (nav), `src/PrivatePaste.Server/wwwroot/site.css`, `site.js`
- Create: `src/PrivatePaste.Server/Pages/Settings.cshtml(.cs)`
- Test: `tests/PrivatePaste.Tests/Web/` (follow the existing page tests)

**Interfaces:**
- Consumes: Task 2 endpoints/service methods.
- Produces: History page with two tabs, "History" and "Pinned" (`?tab=pinned`), search applies to the open tab; each row gets a Pin/Unpin control (icon button with a text label for screen readers) and pinned rows show a small pin marker in History. Nav gains "Settings" between Devices and Sign out. Settings page: "Auto-delete history" select (Off / After 7 days / After 30 days / After 90 days, saved on change with a "Saved" note) and a "Clear history" button that asks "Delete N clips from every device? Pinned clips stay." before posting. Pin icon: a 24×24 stroked path added to `branding/` (`branding/ui-icons/pin.svg`), stroke 2 like the platform icons; list it in `branding/README.md` and the Icons section of `DESIGN.md`.

- [ ] **Step 1: Write the failing tests**: Pinned tab lists only pinned clips; a pinned clip shows the marker in History; Settings page renders the current auto-delete value; posting Clear history deletes unpinned clips.
- [ ] **Step 2–4:** run (fail), implement (reuse `asp-append-version`; bump nothing else), run all tests (pass). Check both themes and a phone-width layout in a browser against a local dev server.
- [ ] **Step 5: Commit** `feat(web): Pinned tab, pin controls, clear history and auto-delete settings`.

### Task 5: Desktop core — private copies, blocklist, pins in the cache and API

**Files:**
- Modify: `src/PrivatePaste.Client.Core/Platform/ClipboardContent.cs`, `src/PrivatePaste.Client.Core/Sync/ClipboardCoordinator.cs`, `src/PrivatePaste.Client.Core/Cache/HistoryCache.cs`, `src/PrivatePaste.Client.Core/Api/PrivatePasteApi.cs`, `src/PrivatePaste.Client.Core/Settings/ClientSettings.cs`
- Test: `tests/PrivatePaste.Tests/Client/ClipboardCoordinatorTests.cs`, `HistoryCacheTests.cs`, `PrivatePasteApiTests.cs`, `ClientSettingsTests.cs` (extend existing files)

**Interfaces:**
- Produces:
  - `ClipboardContent` gains `public bool IsPrivate { get; init; }` and `public string? SourceApp { get; init; }` (executable/bundle name without path or extension, lowercase; null when unknown).
  - `ClipboardCoordinator` gets `Func<string, bool> isBlockedApp` (constructor parameter; desktop passes a lookup over the settings list). On a read with `IsPrivate` or a blocked `SourceApp`: nothing is uploaded, queued, cached or hashed; `LastLocalCaptureAt` is still set to the read time; one log line from Global Constraints; new public event `PrivateCopySkipped` (no payload) for the UI.
  - `ClipboardCoordinator.RecentSourceApps -> IReadOnlyList<string>`: the distinct `SourceApp` values of the last 10 reads (private or not), newest first, in memory only.
  - `ClientSettings` gains `IReadOnlyList<string> BlockedApps` (default empty; older settings files load as empty).
  - Queued uploads rejected with 410 are dropped without a toast (other 4xx keep today's toast).
  - `HistoryCache` trimming to its capacity never removes a clip whose `PinnedAt` is set; `HistoryCache.Pinned -> IReadOnlyList<CachedClip>` newest pinned first.
  - `PrivatePasteApi.PinAsync(Guid id, bool pinned, CancellationToken ct = default) -> Task<ClipDto>`, `ListPinnedAsync(string? query, string? cursor, int limit = 50, CancellationToken ct = default) -> Task<ClipPage>`.

- [ ] **Step 1: Write the failing tests**: `A_private_copy_is_not_uploaded_queued_or_cached`, `A_copy_from_a_blocked_app_is_skipped_case_insensitively`, **`A_private_copy_still_counts_as_the_newest_local_copy`** (Review Focus 1: after it, a catch-up of an older remote clip writes nothing), `RecentSourceApps_keeps_the_last_ten_distinct_newest_first`, `A_410_drops_the_queued_clip_without_a_toast`, **`Trimming_keeps_pinned_clips_beyond_capacity`** (Review Focus 5), `Pinned_is_newest_pinned_first`, **`Catch_up_after_a_pin_applies_nothing`** (Review Focus 2, in `SyncEngineTests`), settings round-trip of `BlockedApps` and loading an old file without it.
- [ ] **Step 2–4:** run (fail), implement, run all tests (pass).
- [ ] **Step 5: Commit** `feat(client): skip private copies and blocked apps; keep pins in the cache`.

### Task 6: Desktop detection — Windows markers and source app

**Files:**
- Create: `src/PrivatePaste.Desktop/Platform/Windows/PrivateCopyMarkers.cs`
- Modify: `src/PrivatePaste.Desktop/Platform/Windows/WindowsClipboard.cs`, `Win32Clipboard.cs`
- Test: `tests/PrivatePaste.Tests/Client/PrivateCopyMarkersTests.cs`, `tests/PrivatePaste.Tests/Client/WindowsClipboardTests.cs` (Windows-only, early return elsewhere, like the title-bar tests)

**Interfaces:**
- Consumes: `ClipboardContent.IsPrivate/SourceApp` (Task 5).
- Produces: `PrivateCopyMarkers.IsPrivate(IReadOnlyDictionary<string, byte[]?> formatsByName) -> bool`: true when `ExcludeClipboardContentFromMonitorProcessing` or `Clipboard Viewer Ignore` is present, or `CanIncludeInClipboardHistory` / `CanUploadToCloudClipboard` holds a little-endian DWORD 0. `WindowsClipboard.ReadAsync` fills `IsPrivate` (checking only those four registered formats, reading the two DWORDs) and `SourceApp` (clipboard owner window's process via `GetClipboardOwner`; if 0, the foreground window's process recorded when the change was seen).

- [ ] **Step 1: Write the failing tests**: the pure `IsPrivate` cases (each marker alone; DWORD 1 → not private; DWORD 0 → private; none → not private); on Windows, put text plus each marker format on the real clipboard from the test and assert `ReadAsync` reports `IsPrivate` and the test process name as `SourceApp`. Restore the clipboard afterwards.
- [ ] **Step 2–4:** run (fail), implement, run all tests (pass).
- [ ] **Step 5: Commit** `feat(desktop): honour Windows private-copy markers; know which app copied`.

### Task 7: Desktop detection — macOS and Linux (best effort)

**Files:**
- Modify: `src/PrivatePaste.Desktop/Platform/Polling/AvaloniaPollingClipboard.cs` (and `ClipboardSelection.cs` if the format list lives there)
- Create: `src/PrivatePaste.Desktop/Platform/Mac/FrontmostApp.cs`, `src/PrivatePaste.Desktop/Platform/Linux/X11SelectionOwner.cs`
- Test: `tests/PrivatePaste.Tests/Client/PrivateCopyMarkersTests.cs` (extend with the pure checks)

**Interfaces:**
- Produces: `PrivateCopyMarkers.IsPrivateMac(IEnumerable<string> types) -> bool` (`org.nspasteboard.ConcealedType`, `org.nspasteboard.TransientType`), `PrivateCopyMarkers.IsPrivateLinux(IEnumerable<string> targets, Func<string, string?> readText) -> bool` (`x-kde-passwordManagerHint` whose value is `secret`). `FrontmostApp.BundleId() -> string?` (NSWorkspace via the Objective-C runtime) for `SourceApp` on macOS; `X11SelectionOwner.ProcessName() -> string?` (libX11: CLIPBOARD owner → `_NET_WM_PID` → process name; null on Wayland or on failure). `PlatformServices` reports `CanIdentifySourceApp` (Windows true; macOS true; Linux true only on X11 when the call works).

- [ ] **Step 1: Write the failing tests** for the two pure functions.
- [ ] **Step 2–4:** run (fail), implement, run all tests (pass). Verify on the CI runners only what can be verified (build, pure tests); note in the task report that macOS/Linux marker visibility through Avalonia is unverified on real machines. If Avalonia does not expose the format list on a platform, leave `IsPrivate` false there and record it for the docs (Task 10).
- [ ] **Step 5: Commit** `feat(desktop): private-copy markers and source app on macOS and Linux (best effort)`.

### Task 8: Desktop UI — popup tabs and pins, blocklist settings

**Files:**
- Modify: `src/PrivatePaste.Desktop/Views/PopupWindow.axaml(.cs)`, `src/PrivatePaste.Desktop/ViewModels/PopupViewModel.cs`, `src/PrivatePaste.Desktop/Input/PopupKeyRouter.cs`, `src/PrivatePaste.Desktop/Views/SettingsWindow.axaml(.cs)`, `src/PrivatePaste.Desktop/ViewModels/SettingsViewModel.cs`, `src/PrivatePaste.Desktop/App.axaml.cs`, `src/PrivatePaste.Desktop/Theme/NightVault.axaml` (pin icon geometry from `branding/ui-icons/pin.svg`)
- Test: `tests/PrivatePaste.Tests/Client/PopupKeyRouterTests.cs`, `PopupViewModelTests.cs`, `SettingsViewModelTests.cs`, `DesktopRenderTests.cs`

**Interfaces:**
- Consumes: Task 5 (`HistoryCache.Pinned`, `PrivatePasteApi.PinAsync`, `RecentSourceApps`, `BlockedApps`), Task 6/7 (`CanIdentifySourceApp`).
- Produces:
  - `PopupKeyAction.SwitchTab` (plain Tab) and `PopupKeyAction.TogglePin` (Ctrl+P); the existing test that Tab is swallowed changes to SwitchTab; Ctrl+P stops being swallowed.
  - `PopupViewModel.Tab` (`PopupTab.History | PopupTab.Pinned`), `SwitchTab()`, `TogglePinSelectedAsync()`; Pinned tab lists `HistoryCache.Pinned`, search filters the open tab; `PopupItem.IsPinned`.
  - Popup: History/Pinned tab strip under the search box (not focusable; click switches), a pin marker on pinned rows, a pin button on each row (click toggles, does not paste). The footer hint becomes "Enter to paste  Tab for Pinned  Ctrl+P to pin" (or "Tab for History" on the Pinned tab).
  - Settings window: a "Private copies" section: "Ignore copies from these apps" list with Remove, and an "Add" menu of `RecentSourceApps` not yet in the list plus "Type a name…". Hidden when `CanIdentifySourceApp` is false, replaced by "This system can't tell which app copied something, so apps can't be blocked here. Copies that password managers mark as private are still ignored." A toast "Skipped a private copy." is not shown (silent by design).

- [ ] **Step 1: Write the failing tests**: router (Tab → SwitchTab, Ctrl+P → TogglePin, Ctrl+Shift+P still swallowed); VM (tab switch keeps the query; Pinned lists newest pinned first; toggle pin calls the API and updates `IsPinned`); settings VM (add from recent, add typed name normalised to lowercase without `.exe`, remove, saved to `ClientSettings.BlockedApps`); render both themes of the popup on each tab and of the settings window (extend `Readme_popup`'s fixture handling only if `demo.json` gains a `pinned` field, see Task 10).
- [ ] **Step 2–4:** run (fail), implement, run all tests (pass). By hand, with an isolated build (`--data-dir`) against a local server and the installed app quit: the popup still never takes the focus, Tab/Ctrl+P work, pins sync to the web UI.
- [ ] **Step 5: Commit** `feat(desktop): Pinned tab and pins in the popup; blocked apps in Settings`.

### Task 9: Android — pins, Pinned tab, long-press menu, quiet 410

**Files:**
- Modify: `android/app/src/main/java/xyz/ficdev/privatepaste/core/model/Protocol.kt`, `api/PrivatePasteApi.kt`, `storage/HistoryCache.kt`, `storage/OutboundQueue.kt` (or the sender that flushes it), `ui/history/HistoryScreen.kt`, `HistoryViewModel.kt`, `ClipRow.kt`, `res/values/strings.xml`, `res/drawable/ic_pin.xml`
- Test: `android/app/src/test/java/xyz/ficdev/privatepaste/...` (extend the existing tests for each file), `ui/ScreenshotTest.kt`

**Interfaces:**
- Produces: `ClipDto.pinnedAt: String? = null` (ISO instant, same as the other time fields); `PrivatePasteApi.pin(id: String, pinned: Boolean): ClipDto`; `HistoryCache` keeps pinned clips beyond `max`, `pinned(): List<ClipDto>` newest pinned first; queued clips rejected with 410 are dropped without a notification; History screen gets "History"/"Pinned" tabs under the search field; long-press opens a menu "Pin"/"Unpin" and "Delete" (Delete keeps today's confirmation behaviour); pinned rows show the pin marker.

- [ ] **Step 1: Write the failing tests**: decoding a ClipDto with and without `pinnedAt`; **cache keeps pins beyond 200** (Review Focus 5); `pinned()` order; 410 drop; view model tab switching and pin toggle; screenshots of both tabs in both themes.
- [ ] **Step 2–4:** run (fail), implement, run `./gradlew testDebugUnitTest` (pass), and the local e2e test against a local server.
- [ ] **Step 5: Commit** `feat(android): Pinned tab, pin from the long-press menu`.

### Task 10: Docs, README and hero

**Files:**
- Modify: `README.md`, `docs/desktop.md`, `docs/android.md`, `docs/self-hosting.md`, `scripts/readme/demo.json` (mark two clips `"pinned": true` if the hero should show the marker; the readers in `DesktopRenderTests.Readme_popup`, `ReadmeScreenshotTest` and `capture.mjs` set `PinnedAt`/pin via the API for them), `docs/images/hero-*.png`, `CLAUDE.md` (platform-marker facts and the 410 rule under Gotchas)

- [ ] **Step 1:** Update the README feature list ("Private copies stay private", "Pinned clips", clearing/auto-delete) and Security section; `docs/desktop.md` gets a "Private copies" section with a table to fill from the owner's tests: Bitwarden, 1Password, KeePass/KeePassXC, LastPass × desktop app / browser extension → "Skipped automatically" / "Add it to the blocklist" / "Can't be caught" (leave cells "Not tested yet" until the owner reports), plus the macOS/Linux status from Task 7; `docs/android.md` gets pins and the long-press menu; `docs/self-hosting.md` mentions Clear history and auto-delete.
- [ ] **Step 2:** Run `powershell -File scripts/readme/build.ps1` and look at both hero images.
- [ ] **Step 3:** Run all .NET and Android tests.
- [ ] **Step 4: Commit** `docs: private copies, pins and history clearing`.

## After the plan

Hand the owner a test build (isolated desktop run against a local server, or an RC tag) for the password-manager table; fill `docs/desktop.md` from the results. Before merging to `main`, remind the owner to back up `/data` (schema change). Release: `server-v0.2.0` image, `desktop-v0.2.0`, `android-v0.2.0`, and bump the Dokploy template image tag.
