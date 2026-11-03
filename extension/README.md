# PrivatePaste for Edge

The browser extension client, for machines where the desktop app can't be installed. Design:
`docs/superpowers/specs/2026-11-02-browser-extension-design.md`.

## Build

```
node extension/build.mjs [--version X.Y.Z] [--fake-clipboard] [--out <dir>]
```

Writes the unpacked extension to `extension/dist/` (or `--out`). There is no bundler: `src/` is copied as is, and the
crypto is copied in from the web UI (`src/PrivatePaste.Server/wwwroot/js/e2e.js` to `js/e2e.js`, and
`wwwroot/lib/hash-wasm/` to `lib/hash-wasm/`), so there is only one copy of it in the repo. The popup's Sora font
comes the same way (`wwwroot/fonts/` to `fonts/`). `--version` defaults to
`0.0.0`; a prerelease such as `0.6.0-rc1` becomes `version` `0.6.0` and `version_name` `0.6.0-rc1`.

`--fake-clipboard` replaces `platform/clipboard.js` with `platform/fake-clipboard.js`, an in-memory clipboard that
tests drive with the runtime messages `{type: 'test.clipboard.set', text?, pngBase64?}` and
`{type: 'test.clipboard.get'}`. Automated tests must use it: the real clipboard is synced by the owner's desktop app.

To try a build: `edge://extensions`, turn on Developer mode, Load unpacked, pick the output folder.

## Tests

- Unit tests: `node --test "extension/test/*.test.mjs"` (Node 22 doesn't take a folder here, so pass the glob).
- Real Edge, by hand: `node extension/test/clipboard.playwright.mjs [--seconds 60]` builds with the real clipboard,
  loads it into a minimized Edge with a throwaway profile, and checks that the extension reads the current clipboard
  exactly (text against `Get-Clipboard`, a PNG against the clipboard's PNG format), that Argon2 runs under the
  extension's CSP, and what polling costs. It only reads the clipboard and prints no content. Needs Playwright from
  `scripts/readme` (`npm ci` there once). The text and image checks skip when the clipboard holds no text or no PNG,
  so before a release run it once with text on the clipboard and once with a large screenshot (it reports ms per read).
- Popup, real Edge, by hand: `node extension/test/popup.playwright.mjs [--url <fresh local server>]` starts its own
  local server (`PP_PASSWORD=dev`, a throwaway data folder) unless `--url` is given, builds with the fake clipboard,
  sets up encryption on the web UI, opens the toolbar popup (`chrome.action.openPopup`), which hands sign-in over to a
  tab, and drives `popup.html` there: failed sign-ins and their messages, sign in, unlock, rows, chips, search, tabs,
  Copy, Download, Pause syncing, Sign out. The image Copy's `navigator.clipboard.write` is stubbed, so the real clipboard is never
  written. With `PP_SCREENSHOT_DIR` set it saves the popup in dark and light.
- Real Edge, by hand: `node extension/test/socket.playwright.mjs` starts its own local server (throwaway data), loads a
  fake-clipboard build and checks that the sync socket opens with the subprotocol token and that a clip from another
  device reaches the (fake) clipboard through it. `PP_WS_DEBUG=1` adds the server's request log.
- End to end, real Edge, by hand: `node extension/test/e2e.playwright.mjs` starts its own local server, a fake-clipboard
  build and a second device through the API, and checks both directions within 3 s (no echo), that Edge restarted on
  the same profile syncs without the passphrase and applies a clip from meanwhile once, and that a copy made while the
  server is down arrives as a backfill. These scripts share `extension/test/helpers/local.mjs`.

## How it works

- `sw.js`, the service worker, only keeps the offscreen document open: it creates it on install, on browser startup and
  from a one-minute alarm.
- `offscreen.html` / `offscreen.js`, the offscreen document (reason `CLIPBOARD`), hosts what keeps running: the
  clipboard poller (`core/poller.js`, once a second) and the sync engine (`engine-host.js`).
- `popup.html` / `popup.js`, the toolbar popup (the manifest's `action`), holds no keys: it asks the engine through runtime messages
  (`messages.js`, answered by the offscreen document; bytes cross as base64) and draws the rows with `ui/rows.js`.
  It asks for the host permission of the server's origin in the Sign in click. Edge closes the toolbar popup as soon as
  that prompt opens, which would end the sign-in with no message, so while signed out the popup opens the same page in a
  tab (or brings an open one forward) and closes itself: the tab stays open behind the prompt and shows what failed.
  Every sign-in failure is a plain message, in the desktop app's words where it has one. Copy on an image row is written by the
  popup itself (`navigator.clipboard.write`), after telling the engine so the poller doesn't send it back.
  While open it refreshes every 2 s, asking only for the status and `engine.historyVersion` (a counter the engine bumps
  on every cache change); rows are fetched and decrypted only when that moved. Downloads go through a blob URL made by
  the offscreen document, so no file crosses the message channel.
- `platform/clipboard.js` reads through `document.execCommand('paste')` into a contenteditable (plain text and the
  first `image/png` item) and writes text through a `copy` event and `execCommand('copy')`. The async Clipboard API
  doesn't work there, because an offscreen document never has the focus.
- Images can't be written from the offscreen document (measured in Edge, 2026-11-02), so `writeImage` returns false:
  incoming images go to the history, and the popup's Copy writes them while the popup has the focus.
- Each read pulls the whole image, so when reading and hashing take long (a big screenshot on the clipboard) the next
  read waits ten times as long, keeping the poller near a tenth of a core at worst; small clips are read every second.
- The poller keeps only SHA-256s of the last read (text and image hashed apart). The first read after start isn't
  reported, the same as the desktop app, which doesn't upload what was on the clipboard before it started.

## Release

For the repository owner: push a tag such as `extension-v0.1.0`. `.github/workflows/extension.yml` runs the tests, builds with the version from the tag, zips the result as `privatepaste-extension-<version>.zip` and attaches it to a GitHub Release (a version with a `-`, like `0.2.0-rc1`, becomes a prerelease).

Edge Add-ons: that zip is what gets uploaded to Partner Center. The listing texts, the images (`store/make-images.mjs`) and the step-by-step for submitting and updating are in `store/` (start with `store/README.md`).
