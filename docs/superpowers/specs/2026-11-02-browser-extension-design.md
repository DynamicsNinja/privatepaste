# Browser extension

A PrivatePaste client that runs as an Edge extension, for machines where the desktop app can't be installed.

## Why

The owner's work laptop is IT-managed: Defender's attack-surface-reduction rule blocks new unsigned exes, so the desktop app may not run there, and an exception can be taken away. The same laptop allows Edge extensions, from the Edge Add-ons store and in developer mode. On such a machine the extension takes the desktop app's place: "if I can't install the desktop one, it saves the day."

No self-hosted clipboard sync offers a browser extension (ClipCascade has desktop and Android apps only); Pushbullet and Join have extensions but are hosted services.

## What success looks like

On the work laptop, with only the extension installed, the clipboard behaves as if the desktop app were there: a copy in Outlook, Teams or VS Code shows up on the phone a moment later, and a copy on the phone pastes on the laptop with Ctrl+V. The owner never opens the extension for that to happen.

## Goals

1. **Copies sync on their own** (the owner's top priority). Text and links copied in any app, not only in the browser, are picked up within about a second and uploaded. Images too, if writing and reading them passes the first spike (see Risks).
2. **Incoming live clips go onto the clipboard**, like the desktop app: the newest live clip from another device replaces the clipboard, so Ctrl+V pastes it. Catch-up, backfill and pin changes follow the same rules as the desktop app (`Client.Core` `SyncEngine` / `ClipboardCoordinator` are the reference; `CLAUDE.md` Gotchas on pins and `lastCopiedAt`).
3. **Toolbar popup with full history** (owner chose option A of two mockups). It opens from the PrivatePaste icon in Edge's toolbar and mirrors the desktop popup:
   - search, History and Pinned tabs, the type chips (All · Text · Links · Images · Files);
   - Copy on every row; Download on file rows;
   - the status ("Syncing", "Paused", "Offline", "Locked") and a settings entry.
4. **Files arrive in the history and download from there** (owner's answer). An extension can't put files on the Windows clipboard, so a file clip is never applied to the clipboard; its row offers Download (each file, into Downloads, through the `downloads` API or a link). Files copied in Explorer can't be read by the extension and aren't sent.
5. **Shows as a browser extension** (owner's answer): a new platform string `extension`, with its own icon, in Devices and in history rows on every client. The default device name is "Edge on <computer name>" when the name can be found, otherwise "Edge"; it can be renamed like any device.
6. **Sign-in like a new desktop device:** server address, sign in, then unlock with the passphrase. Claude's call: passphrase only in this round. The laptop has no camera to scan a QR, and no client offers a handover as text to paste; adding that would touch every client and risk the key-bearing code landing in synced history.
7. **Stays unlocked like the desktop app**, not per browser session like the web UI: after Edge restarts it keeps syncing without asking for the passphrase again.

## Non-goals

- Firefox. Chrome is a free side effect (same engine) but isn't tested or published in this round.
- A Ctrl+Shift+V style popup over other apps. Extensions can't open a window outside the browser, and a global shortcut is limited; the owner chose automatic syncing over this.
- Sending files, rich text (release 3 of the E2E spec), or anything copied while Edge isn't running.
- Store automation: the first Edge Add-ons submission and later uploads are done by hand.

## Anti-goals

Claude proposed these; the owner confirmed them, except that a password copied from a password manager syncing is acceptable (see Risks 1). It fails, even if it works, when:

- it slows Edge or drains the laptop's battery by checking the clipboard every second;
- it misses copies, so something copied never reaches the phone;
- it pastes something unexpected, for example an incoming clip replacing what was just copied on the laptop (the desktop app's newest-wins and echo rules must hold);
- it asks for the passphrase again on every Edge restart;
- any clip content, passphrase or key reaches a log, the extension's console output in a release build, or the server in plaintext (the E2E spec's anti-goals apply unchanged).

## Constraints

- **Edge first.** Manifest V3. The spike (below) ran in Edge 2026 on this machine.
- **Protocol 2, end to end encrypted**, exactly as the other clients: vault unlock (Argon2id), AES-256-GCM payloads and chunked blobs, keyed hash, `X-PrivatePaste-Protocol: 2` on every request and the WebSocket, `GET /api/server-info` at sign-in and on every reconnect (kinds and limits govern what is sent). Reuse `src/PrivatePaste.Server/wwwroot/js/e2e.js` and its vendored Argon2 WASM rather than writing a third crypto port; the extension must pass the same `docs/e2e-test-vectors.json` vectors.
- **Content hash** (`docs/api.md`, "Content hash"): text is `t:` over normalized text; images hash RGBA8888 **unpremultiplied** pixels. A canvas `getImageData` premultiplies internally, so semi-transparent pixels can round differently; the builder must decode with `createImageBitmap(..., {premultiplyAlpha: 'none'})` or an equivalent and prove it against the image vectors, or echo prevention and dedup break for images with transparency.
- **Never use the live server** for tests or screenshots (`CLAUDE.md`). Tests that read or write the real OS clipboard must not run while the owner's installed app is syncing to the live server; reading only is fine.
- **No server change is needed for sign-in**: the server stores any platform string. Clients that don't know `extension` show an unknown device today, so the desktop, Android and web UIs get the icon in the same release, and the platform list in `CLAUDE.md` and `docs/api.md` gains `extension`.
- **Copy and look** follow `DESIGN.md` (Night Vault, sentence case, plain copy); the popup is a smaller sibling of the desktop popup. Render checks in both themes, like the other UIs.
- **Releases:** a tag `extension-vX.Y.Z` builds and tests in CI and attaches the packed `.zip` to a GitHub Release; versions stay identical to the other apps (owner's preference). README, `docs/` and the homepage gain an extension section and download.

## How the extension reads and writes the clipboard (proven by spike)

A throwaway extension (2026-11-02) showed, in both Chromium and the real Edge **on the owner's IT-managed work laptop itself** (domain- and Entra-joined; its Edge policies set only relaunch notices, local-network access for SharePoint and a WebView2 flag, nothing about extensions, developer mode or clipboard), with the window minimized and no page focused:

- an **offscreen document** (`chrome.offscreen`, reason `CLIPBOARD`) with the `clipboardRead` permission reads the OS clipboard through `document.execCommand('paste')` into a textarea; the text matched the OS clipboard exactly;
- `navigator.clipboard.readText()` there is refused (`NotAllowedError`), so the paste-command route is the one to use;
- headless browsers read nothing (no real clipboard), so automated tests of this layer need a headed browser or a fake.

Unproven and left to the first build step: writing text and images back (`execCommand('copy')` from the offscreen document), reading images (paste into a contenteditable), keeping the offscreen document alive and polling about once a second without the 30-second alarm floor, and the CPU cost of that polling.

## Risks

1. **Password-manager copies.** The desktop app skips copies marked private by password managers (`Platform/PrivateCopyMarkers.cs`: on Windows, formats such as `ExcludeClipboardContentFromMonitorProcessing` and `CanUploadToCloudClipboard`). The extension only sees plain text and images, never those formats, so it can't tell a password from any other copy. **Owner's decision: acceptable.** Such copies sync like any other (end-to-end encrypted, so the server still can't read them). The extension's docs and its settings screen say so plainly ("Copies from password managers sync too: the extension can't tell them apart"). As Claude's call, the popup gets a "Pause syncing" switch, so the owner can stop it before copying something sensitive.
2. **Image round trip** is unproven (above). If images can't be read or written reliably, they drop to "history only" like files.
3. **Polling cost.** If one-second polling costs noticeable CPU or battery, the interval backs off when nothing changes, and the spec's "about a second" becomes a goal the builder measures, not a promise.
4. **Edge store review** may question `clipboardRead`; the listing must say plainly why it's needed. It isn't on the critical path: the work laptop allows developer mode, so v1 is usable as an unpacked or sideloaded build before the store accepts it.
5. **IT policy can change.** Today nothing restricts extensions on the work laptop; a future policy could. That risk is the same one the desktop app has with Defender, and nothing in this design can remove it.

## Left to the builder

- The extension's file layout and build (plain ES modules copied from `wwwroot/js` versus a bundler), and how `e2e.js` is shared without duplicating it.
- Where the vault key and device token are kept so they survive restarts (`chrome.storage.local`, wrapped by a non-extractable WebCrypto key in IndexedDB, or similar), within the anti-goals.
- The popup's exact layout within the chosen mockup, its size, and the settings and sign-in screens.
- How the computer name is found for the default device name (it may not be available to an extension; fall back to "Edge").
- Polling interval tuning and back-off.
