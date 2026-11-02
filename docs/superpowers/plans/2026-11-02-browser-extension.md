# Browser Extension Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An Edge (Manifest V3) extension that is a full PrivatePaste client: it picks up copies made in any app, syncs them end to end encrypted, puts incoming live clips on the clipboard, and shows the history in a toolbar popup.

**Architecture:** A long-lived **offscreen document** hosts everything that must keep running: the clipboard poller (the spike's `execCommand('paste')` route), the sync engine (fetch, WebSocket, catch-up, queue) and IndexedDB state. The service worker only keeps the offscreen document alive and opens nothing else. The popup is a separate page that talks to the engine through `chrome.runtime` messages and decrypts history itself. All logic lives in plain ES modules under `extension/src/core/` with injected dependencies, so it runs under `node --test` without a browser; the crypto is the web UI's `e2e.js`, copied in at build time, never forked.

**Tech Stack:** Plain JavaScript ES modules, no bundler; `node --test` for unit tests; Playwright (from `scripts/readme/node_modules`) driving the real Edge (`channel: 'msedge'`) for end-to-end tests; WebCrypto; the vendored `hash-wasm` Argon2.

**Spec:** `docs/superpowers/specs/2026-11-02-browser-extension-design.md` (approved 2026-11-02). Protocol rules: `docs/api.md` ("Client rules" is the authority for sync behaviour).

## Global Constraints

- Edge, Manifest V3; Chrome and Firefox are not targets.
- Protocol 2 exactly as `docs/api.md`: `X-PrivatePaste-Protocol: 2` on every request including the WebSocket upgrade; `GET /api/server-info` at sign-in and on every reconnect; create only kinds in `kinds`; every size check and message uses `limits` (`fileMb` missing → 25).
- Crypto only through `src/PrivatePaste.Server/wwwroot/js/e2e.js` and `wwwroot/lib/hash-wasm/`, copied into the build; no second copy in the repo. Must pass `docs/e2e-test-vectors.json` and `docs/classifier-test-vectors.json`.
- Platform string `extension`; default device name `Edge` (editable at sign-in; an extension can't read the computer name).
- Unlock with the passphrase only. No handover QR in or out.
- Stays unlocked across Edge restarts: the vault is kept as **non-extractable** WebCrypto keys in IndexedDB, never as raw bytes; the raw key is dropped right after unlock.
- Sign-out or revoke deletes token, vault keys, history cache and queue.
- Never log or `console.*` clip content, passphrases, keys or tokens; release builds strip debug logging.
- Copy (sentence case, `DESIGN.md`): status `Syncing`, `Paused`, `Offline`, `Locked`; switch `Pause syncing`; settings note `Copies from password managers sync too: the extension can't tell them apart.`; existing strings from `docs/api.md` verbatim (`Update your PrivatePaste server to keep syncing.`, `Update PrivatePaste to keep syncing.`, `Encryption was reset. Enter the new passphrase.`, `Ask the owner to set up encryption.`, `Update PrivatePaste to see this clip`).
- Files are never put on the clipboard; file rows offer Download. Files copied in Explorer are not sent.
- **Never use the live server** (https://paste.ficdev.xyz). Local server only: `PP_DB_KEY=0123456789abcdef0123 PP_PASSWORD=dev PP_DATA_DIR=<throwaway> dotnet run --project src/PrivatePaste.Server`.
- **Never write to the real OS clipboard in automated tests** (the owner's installed app syncs it to the live server). Reading it is allowed. Automated end-to-end tests use the fake clipboard (Task 1's `fakeClipboard` build flag).
- Versions match the other apps; the manifest's `version` comes from the release tag.

## Review Focus

1. **Echo:** an incoming clip the extension writes to the clipboard is read back on the next poll; it must not be uploaded again or re-applied. → Task 2, `engine.test.mjs` "own write is not uploaded".
2. **A fresh local copy beats a slightly older incoming clip:** a live clip whose `lastCopiedAt` is earlier than the server time of the latest local upload goes to history only. → Task 2, "older live clip does not overwrite a newer local copy".
3. **Restart:** after the offscreen document or Edge restarts, sync resumes without the passphrase, catch-up applies at most the newest live clip, and only if newer than the last applied. → Task 2 "catch-up applies only the newest", Task 6 "survives restart".
4. **Offline:** copies made while the server is unreachable are queued and uploaded later as `backfill: true`; 410 and `Encryption was reset` 409 drop them quietly. → Task 2 "queued copies backfill", "410 drops quietly".
5. **Oversized or unreadable content:** text over `textKb`, images over `imageMb` or 50 MP, and clipboard content that is neither text nor PNG (an Explorer file copy) are skipped without an error loop, and the status says why for size. → Task 2 "oversized text is skipped once".

---

### Task 1: Scaffold, build, offscreen clipboard

Proves and wraps the platform layer. Everything later builds on `clipboard.js` and `build.mjs`.

**Files:**
- Create: `extension/manifest.template.json`, `extension/build.mjs`, `extension/src/sw.js`, `extension/src/offscreen.html`, `extension/src/offscreen.js`, `extension/src/platform/clipboard.js`, `extension/src/platform/fake-clipboard.js`, `extension/README.md`
- Create: `extension/test/build.test.mjs`, `extension/test/clipboard.playwright.mjs`
- Modify: `.gitignore` (add `extension/dist/`)

**Interfaces:**
- Produces: `node extension/build.mjs [--version X.Y.Z] [--fake-clipboard] [--out <dir>]` → unpacked extension in `extension/dist/` (default), with `js/e2e.js` and `lib/hash-wasm/*` copied from `src/PrivatePaste.Server/wwwroot/`.
- Produces (`platform/clipboard.js`, used only inside the offscreen document): `readClipboard(): Promise<{text?: string, png?: Uint8Array} | null>`, `writeText(text: string): Promise<boolean>`, `writeImage(png: Uint8Array): Promise<boolean>`. `fake-clipboard.js` exports the same three functions backed by an in-memory value plus `chrome.runtime` test messages `{type:'test.clipboard.set', text?|pngBase64?}` and `{type:'test.clipboard.get'}`.
- Produces (`offscreen.js`): `startPolling(onChange: (item) => void, intervalMs = 1000)`; it compares a SHA-256 of what it read with the previous read and calls `onChange` only on a change. The first read after start sets the baseline and is **not** reported (matching the desktop app, which doesn't upload what was on the clipboard before it started). `offscreen.js` then calls `startEngine({clipboard, startPolling})` from `./engine-host.js` (Task 2 creates that file; until it's merged, Task 1 ships a stub `engine-host.js` that only starts polling and drops changes).

- [ ] **Step 1: Write the failing build test** `extension/test/build.test.mjs`: running `build.mjs --version 1.2.3 --out <tmp>` produces `manifest.json` with `version` `"1.2.3"`, `manifest_version` 3, permissions exactly `["offscreen","clipboardRead","clipboardWrite","alarms"]`, `optional_host_permissions` `["http://*/*","https://*/*"]`, CSP `extension_pages` = `"script-src 'self' 'wasm-unsafe-eval'; object-src 'self'"`, and `js/e2e.js` byte-identical to `src/PrivatePaste.Server/wwwroot/js/e2e.js`; with `--fake-clipboard`, `platform/clipboard.js` is the fake.
- [ ] **Step 2: Run** `node --test extension/test/` → FAIL (no build script).
- [ ] **Step 3: Implement** the template, `build.mjs` (copy `src/` → out, copy the two shared assets, write the manifest, swap the clipboard module for the fake when flagged), `sw.js` (create the offscreen document with reason `CLIPBOARD` on install, on startup and on a 1-minute `alarms` watchdog), and `clipboard.js`: reading listens for the `paste` event fired by `document.execCommand('paste')` on a focused contenteditable and takes `clipboardData.getData('text/plain')` and the first `image/png` item; writing text uses a `copy` event with `clipboardData.setData('text/plain', …)` and `execCommand('copy')`.
- [ ] **Step 4: Run** `node --test extension/test/` → PASS.
- [ ] **Step 5: Real-Edge read check** `extension/test/clipboard.playwright.mjs` (headed `msedge`, window minimized, temp profile, real `clipboard.js`): the extension's read of the current clipboard hashes equal to `Get-Clipboard -Raw` with CRLF→LF (read-only; skip with a message if the clipboard holds no text). Also log the offscreen document's CPU over 60 s of 1 s polling (`chrome://` isn't scriptable: use `performance.now()` deltas inside the poll loop and report the busy fraction) and write it to the task report.
- [x] **Step 6 result (done by the controller on 2026-11-02 with the owner's app quit, in real Edge from an offscreen document):** `execCommand('copy')` with a `copy` handler writes text exactly (Unicode included) and it reads back; reading an image works through the `paste` event's `image/png` item (byte-identical PNG); **writing an image does not work** from the offscreen document: `clipboardData.items.add(File)` is silently dropped, `navigator.clipboard.write` fails with "Document is not focused", and `text/html` with a data-URL `<img>` puts only HTML on the clipboard. So `writeImage` returns `false` without trying, and the fallback below applies. The original step, kept for the record:
- [ ] **Step 6: Image and write proof (owner present).** These touch the real clipboard, so the controller asks the owner to quit the installed PrivatePaste for two minutes and runs `clipboard.playwright.mjs --write`: write text then read it back; write a 2×2 PNG with one semi-transparent pixel then read it back; and read an image the owner copies. Record in the report which of `writeText`, `writeImage`, image read work. If `writeImage` can't work from the offscreen document, it returns `false`, and Task 2/3 follow the fallback: incoming images are history-only, and the popup's Copy (a user gesture with focus) writes them through `navigator.clipboard.write`.
- [ ] **Step 7: Commit** `feat(extension): scaffold, build and offscreen clipboard`.

### Task 2: Sync engine core (node-tested)

The extension's equivalent of `Client.Core`. Pure modules with injected `fetch`, `WebSocket`, store, clipboard and clock.

**Files:**
- Create: `extension/src/core/api.js`, `extension/src/core/store.js`, `extension/src/core/classify.js`, `extension/src/core/image.js`, `extension/src/core/codec.js`, `extension/src/core/engine.js`
- Test: `extension/test/api.test.mjs`, `extension/test/classify.test.mjs`, `extension/test/image.test.mjs`, `extension/test/codec.test.mjs`, `extension/test/engine.test.mjs`
- Create: `extension/src/engine-host.js` exporting `startEngine({clipboard, startPolling}): Engine`, which builds the real dependencies (IndexedDB store, `fetch`, `WebSocket`, `Date`) and wires polling to `engine.onLocalChange`. Task 1's `offscreen.js` imports it and calls it (ruling R-X1: Task 2 does not edit `offscreen.js`).
- Modify: `docs/e2e-test-vectors.json` only if Step 1 needs a new image vector (add, never change)

**Interfaces:**
- Consumes: Task 1's clipboard functions and `startPolling`; `e2e.js` exports (`importVaultKey`, `unwrapWithPassphrase`, `sealPayload`, `openPayload`, `encryptBlob`, `decryptBlob`, `keyedHash`, `contentHashText`, `contentHashPixels`, `payloadFiles`).
- Produces `api.js`: `createApi({serverUrl, token?, fetch})` → `{serverInfo(), login(username, password, deviceName), logout(), me(), vault(), clips({cursor, limit, pinned}), sync(afterSeq), upload({meta, thumb?, blobs}), blob(id, n), thumb(id), pin(id), unpin(id), remove(id)}`; every call sends the protocol header and the bearer token; failures throw `ApiError {status, title, detail}`; `login` sends `platform: 'extension'`.
- Produces `store.js`: `openStore(indexedDB)` → `{getSession(), setSession({serverUrl, token, deviceId, deviceName}), getVault(), setVault(vault), getState(), setState({highestSeq, lastAppliedAt, lastLocalAt, paused}), getCache(), putClips(clips), removeClip(id), queue(), enqueue(item), dequeue(id), clearAll()}`. `setVault` stores `{keyId, encKey, hashKey}` as non-extractable `CryptoKey`s (re-import from the raw bytes with `extractable: false`, then drop the raw); history cache capped at 500 clips (the desktop size).
- Produces `classify.js`: `classifyText(text): 'link' | 'text'` (same rule as `ContentClassifier.cs`).
- Produces `image.js`: `pngToRgba(png): Promise<{rgba: Uint8Array, width, height}>` (unpremultiplied, exact), `makeThumb(png): Promise<Uint8Array>` (PNG fitting 256×256).
- Produces `codec.js`: `encodeLocal(item, vault, limits, kinds)` → `{meta, thumb?, blobs, contentHash}` or `{skip: reason}`; `decodeClip(clip, vault)` → `{kind, text?, width?, height?, files?}` or `{kind: 'unknown'}`.
- Produces `engine.js`: `createEngine({api, store, clipboard, clock, WebSocket, onStatus})` → `{start(), stop(), signIn(serverUrl, username, password, deviceName), unlock(passphrase), signOut(), setPaused(bool), onLocalChange(item), history({pinned, cursor}), status()}`. `onStatus(s)` with `s.state` in `'signedOut' | 'locked' | 'syncing' | 'paused' | 'offline' | 'serverTooOld' | 'appTooOld' | 'noVault'` and an optional `message`.

- [ ] **Step 1: Write failing tests.** `classify.test.mjs` runs every case of `docs/classifier-test-vectors.json`. `codec.test.mjs`: `contentHashText` and keyed hash equal the vectors; a text over `limits.textKb × 1024` UTF-8 bytes gives `{skip: 'tooLarge'}`; `decodeClip` of an unknown kind gives `{kind: 'unknown'}`. `image.test.mjs`: `pngToRgba` of every image case in `ContentHashTests.cs` reproduces its hash; if no case has a semi-transparent pixel, first add one to `docs/e2e-test-vectors.json` generated by .NET `ContentHash.ForPixels` (and a matching .NET test) so the browser decode is pinned against alpha rounding. `engine.test.mjs`, with fakes, one test each:
  - "local copy uploads": `onLocalChange({text:'hello'})` → one `upload` with `kind 'text'`, `backfill false`.
  - "own write is not uploaded": a live `clip.created` from another device is written to the fake clipboard; the following `onLocalChange` with the same text uploads nothing.
  - "older live clip does not overwrite a newer local copy": local upload answered with `lastCopiedAt` T; a live clip with `lastCopiedAt` T−1 s arrives → not written, still in history.
  - "own device's clip is not applied" (`sourceDeviceId` = own id).
  - "non-live clip is not applied" (`live: false`, pin change replaces the cached copy).
  - "catch-up applies only the newest": three live clips in `sync` → only the newest is written, and only if newer than `lastAppliedAt`.
  - "file clip is never applied" and "unknown kind is never applied".
  - "queued copies backfill": `upload` rejects with a network error → queued; on reconnect it is sent with `backfill: true` and its original `copiedAt`.
  - "410 drops quietly" and "409 Encryption was reset relocks" (clears vault, cache, queue; status `locked` with the reset message).
  - "vault.reset relocks", "revoked close signs out", "server-info 404 → serverTooOld", "426 → appTooOld", "kind not in server kinds → skip".
  - "paused uploads nothing and applies nothing".
  - "oversized text is skipped once": status shows the size message, and the same content read again doesn't retry.
  - "restart keeps the vault": a new engine over the same store reaches `syncing` without `unlock`.
- [ ] **Step 2: Run** `node --test extension/test/` → the new tests FAIL.
- [ ] **Step 3: Implement** the six modules against the interfaces. The engine reconnects with backoff (1, 2, 5, 10, 30 s), pings every 20 s, re-reads `server-info` on every reconnect, and follows `docs/api.md` "Client rules" line by line. `image.js` decodes PNG itself (inflate via `DecompressionStream('deflate')` and unfiltering) rather than through a canvas, which premultiplies.
- [ ] **Step 4: Run** `node --test extension/test/` → PASS.
- [ ] **Step 5: Commit** `feat(extension): sync engine`.

### Task 3: Popup: sign-in, unlock, history

**Files:**
- Create: `extension/src/popup.html`, `extension/src/popup.js`, `extension/src/popup.css`, `extension/src/icons/` (toolbar icons from `branding/`), `extension/src/ui/*.js` as needed
- Test: `extension/test/popup.playwright.mjs`

**Interfaces:**
- Consumes: Task 2's engine through runtime messages `{type:'engine.status'}`, `{type:'engine.signIn', serverUrl, username, password, deviceName}`, `{type:'engine.unlock', passphrase}`, `{type:'engine.signOut'}`, `{type:'engine.setPaused', paused}`, `{type:'engine.history', pinned, cursor, query}` → `{rows: [{id, kind, text?, files?, thumbPng?, sourceDeviceName, sourceDevicePlatform, sourceUserName, lastCopiedAt, pinnedAt}], nextCursor}` (the engine decrypts; the keys never leave the offscreen document), `{type:'engine.copy', id}` (writes a clip to the clipboard; returns `false` for an image when `writeImage` can't, and the popup then writes it itself), and `{type:'engine.download', id, n}` → the decrypted file bytes and safe name. Add these handlers to `offscreen.js` in this task.
- The popup requests the host permission for the server's origin (`chrome.permissions.request`) inside the Sign in click.

- [ ] **Step 1: Write the failing Playwright test** (headed `msedge`, `--fake-clipboard` build, local server with `PP_PASSWORD=dev`, a vault set up through the web UI first): open `chrome-extension://<id>/popup.html`; sign in as `owner`/`dev` with device name `Edge`; unlock with the passphrase; a text clip uploaded by another device appears as a row with the text icon; the Links chip shows only link rows; search filters; History/Pinned tabs switch; Copy on a text row puts it on the fake clipboard; a file row's Download saves the right bytes; the `Pause syncing` switch shows status `Paused`; the settings view shows the password-manager note; Sign out returns to the sign-in view. Screenshots of the history view in dark and light to `PP_SCREENSHOT_DIR`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the popup as option A of the approved mockup (`.superpowers/brainstorm/13781-1793648576/content/ext-popup.html`): about 380 px wide, Night Vault tokens from `src/PrivatePaste.Server/wwwroot/site.css`, kind icons and chips as on the web UI, row text and file rows from `e2e.js` (`fileRowText`, `matchesFilter`, `nextFilter`, `filterEmptyText`), "Search older clips" when local results run out. Images show their decrypted thumbnail; Copy on an image row uses `navigator.clipboard.write` from the popup.
- [ ] **Step 4: Run** → PASS; look at both screenshots.
- [ ] **Step 5: Commit** `feat(extension): popup`.

### Task 4: `extension` platform on every client

Independent of Tasks 1–3; runs in parallel.

**Files:**
- Create: `branding/ui-icons/platform-extension.svg` (a puzzle-piece glyph drawn like the other platform icons)
- Modify: `src/PrivatePaste.Server/Pages/PlatformIcons.cs`, `src/PrivatePaste.Server/wwwroot/js/history.js:13` (`PLATFORMS`), the web platform icon sprite, `src/PrivatePaste.Desktop/Theme/PlatformIcons.cs`, `android/app/src/main/java/xyz/ficdev/privatepaste/ui/history/PlatformIcon.kt`, `CLAUDE.md` (platform strings line), `docs/api.md` (`sourceDevicePlatform` list)
- Test: the existing platform icon tests in `tests/PrivatePaste.Tests` (server and desktop) and the Android `PlatformIcon` test, each gaining an `extension` case

- [ ] **Step 1: Write failing tests:** `extension` maps to the new icon (not unknown) on server, desktop and Android; `EXTENSION` / `Extension` names follow each file's existing style.
- [ ] **Step 2: Run** `dotnet test --filter PlatformIcon` and `./gradlew testDebugUnitTest --tests '*PlatformIcon*'` → FAIL.
- [ ] **Step 3: Implement** the icon on all three plus the web set.
- [ ] **Step 4: Run** both → PASS; desktop and Android render checks (`DesktopRenderTests`, `ScreenshotTest`) of a row from an `extension` device in both themes.
- [ ] **Step 5: Commit** `feat: show browser extension devices`.

### Task 5: Release workflow and docs

After Tasks 1–3 are merged.

**Files:**
- Create: `.github/workflows/extension.yml`, `docs/extension.md`
- Modify: `README.md` (install section, platforms), `docs/self-hosting.md` if it lists clients, `CLAUDE.md` (Layout row for `extension/`, Commands line `node --test extension/test/`, Releases row `extension-vX.Y.Z`), `site/index.html` and its download script (an "Edge extension" download from the release asset), `scripts/readme/README.md` "Text to keep in step"

- [ ] **Step 1:** Workflow on tags `extension-v*`: `node --test extension/test/`, `node extension/build.mjs --version <tag version>`, zip `extension/dist/` as `privatepaste-extension-<version>.zip`, create the GitHub Release with it (same style as `android.yml`). A version with `-` is a prerelease.
- [ ] **Step 2:** `docs/extension.md`: what it does; install unpacked (Developer mode → Load unpacked) and, later, from Edge Add-ons; sign in and unlock; what it can't do (password-manager copies sync too, files arrive in the history only, Explorer file copies aren't sent, only while Edge runs); the `clipboardRead` reason in one plain sentence for the store listing.
- [ ] **Step 3:** README, `CLAUDE.md`, homepage as listed. No "upgrading from older versions" text (owner's rule).
- [ ] **Step 4:** Commit `docs: browser extension` and `ci: extension release workflow`.

### Task 6: End-to-end checks and the owner's hand check

**Files:**
- Create: `extension/test/e2e.playwright.mjs`

- [ ] **Step 1: Write the end-to-end test** (headed `msedge`, `--fake-clipboard` build, local server, a second client simulated through the API with its own device token): fake-clipboard text set in the extension shows up via `GET /api/clips` from the other device within 3 s; a text uploaded by the other device appears on the fake clipboard within 3 s; that write isn't uploaded back; close and reopen the browser context with the same profile → status `syncing` without unlock, and a clip uploaded while it was closed is applied once on reconnect; stop the server, set a text, start the server → it arrives with `backfill: true`.
- [ ] **Step 2: Run** → PASS (fix forward in the owning task's files).
- [ ] **Step 3: Owner's hand check** (for the final report, not automated): with the installed PrivatePaste quit and the extension loaded unpacked from `extension/dist/` against a local server, copy in Notepad, Outlook and VS Code and see each arrive on the phone or web UI; copy on the phone and Ctrl+V on the laptop; download a file from the popup; restart Edge and check it syncs without the passphrase.
- [ ] **Step 4: Commit** `test(extension): end to end`.

## Execution order

- **Parallel wave 1:** Task 1, Task 2 (its engine tests use fakes, so it needs only Task 1's interfaces, not its code), Task 4.
- **Wave 2:** Task 3 (needs Task 2's message interface and Task 1's build).
- **Wave 3:** Task 5, then Task 6.
- Task 1 Step 6 needs the owner for two minutes; the controller asks before running it.
