# PrivatePaste for Edge

The browser extension client, for machines where the desktop app can't be installed. Design:
`docs/superpowers/specs/2026-11-02-browser-extension-design.md`.

## Build

```
node extension/build.mjs [--version X.Y.Z] [--fake-clipboard] [--out <dir>]
```

Writes the unpacked extension to `extension/dist/` (or `--out`). There is no bundler: `src/` is copied as is, and the
crypto is copied in from the web UI (`src/PrivatePaste.Server/wwwroot/js/e2e.js` to `js/e2e.js`, and
`wwwroot/lib/hash-wasm/` to `lib/hash-wasm/`), so there is only one copy of it in the repo. `--version` defaults to
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
  `scripts/readme` (`npm ci` there once).

## How it works

- `sw.js`, the service worker, only keeps the offscreen document open: it creates it on install, on browser startup and
  from a one-minute alarm.
- `offscreen.html` / `offscreen.js`, the offscreen document (reason `CLIPBOARD`), hosts what keeps running: the
  clipboard poller (`core/poller.js`, once a second) and the sync engine (`engine-host.js`).
- `platform/clipboard.js` reads through `document.execCommand('paste')` into a contenteditable (plain text and the
  first `image/png` item) and writes text through a `copy` event and `execCommand('copy')`. The async Clipboard API
  doesn't work there, because an offscreen document never has the focus.
- Images can't be written from the offscreen document (measured in Edge, 2026-11-02), so `writeImage` returns false:
  incoming images go to the history, and the popup's Copy writes them while the popup has the focus.
- The poller keeps only a SHA-256 of the last read. The first read after start isn't reported, the same as the desktop
  app, which doesn't upload what was on the clipboard before it started.
