# PrivatePaste

A self-hosted clipboard sync server. Text and images copied on one device show up on your others. Everything is stored encrypted at rest (SQLite3 Multiple Ciphers database keyed by `PP_DB_KEY`, blobs encrypted with an HKDF-derived key), and no third-party network services are used. History is kept until you delete items.

The API, at a high level: log in with the password to get a device token, then use the token for the clips endpoints (create, list, delete) and for the WebSocket at `/ws`, which pushes live updates. A web UI is served at `/`, and `/health` reports server health. See [API](#api) for the full reference.

## Deploy on Dokploy

1. Create a **Compose** application from this repository (it uses `docker-compose.yml`).
2. Set the environment variables:
   - `PP_DB_KEY` (required, at least 16 characters): encryption key for the database and blobs. It must be a high-entropy random value (see [Generating keys](#generating-keys)), not a memorable passphrase.
   - `PP_PASSWORD` (required): the login password.
   - `PP_MAX_IMAGE_MB` (optional, default 20) and `PP_MAX_TEXT_KB` (optional, default 1024).
   - `PP_TRUST_CLOUDFLARE` (optional, default false). Set to `true` when the server sits behind a Cloudflare tunnel, so the login limit uses the visitor IP from `CF-Connecting-IP` and session cookies are marked Secure (scheme from `Cf-Visitor`). Only enable it if the server is reachable exclusively through Cloudflare; otherwise anyone can set these headers.
3. Add a domain pointing to service `privatepaste`, port **8080**, with HTTPS enabled. Traefik passes WebSockets through by default.
4. Optionally point Dokploy's health check at `/health`. The image has no `curl`, so there is no built-in `HEALTHCHECK`.

Data lives in the named volume `privatepaste-data`, mounted at `/data`.

## Generating keys

```
openssl rand -base64 32
```

Use the output for `PP_DB_KEY` (and a strong, separate value for `PP_PASSWORD`). `PP_DB_KEY` is used directly as the encryption secret, so it has to be random like this; a passphrase you could remember is too weak.

**Back up `PP_DB_KEY`. Without it the data is unrecoverable.**

## Changing the password

Edit `PP_PASSWORD` in the environment and redeploy. The password is hashed with Argon2id at startup.

Existing device tokens keep working after a password change: changing the password does not log devices out. If you are changing it because it leaked, open the **Devices** page in the web UI and revoke every device you don't recognize.

## Backups

1. Get a consistent copy of `/data` (the `privatepaste-data` volume): either stop the container first (`docker compose stop`, copy, `docker compose start`), or take a snapshot of the volume. Copying the files of a running server can catch the database mid-write.
2. Store `PP_DB_KEY` separately and safely (for example in a password manager), not next to the backup. The backup is encrypted with it: **without `PP_DB_KEY` a backup is useless**, and anyone who has both the backup and the key can read everything.

## Proxy and forwarded headers

Login is rate-limited to 5 attempts per minute per client IP (IPv6 clients per /64 prefix). The server trusts `X-Forwarded-For` from the reverse proxy to identify the client IP, so only expose the container through the proxy (Traefik). Do not publish port 8080 directly to the internet, or clients could spoof their IP and bypass the limit.

## API

All JSON is camelCase; enums are lowercase strings (`kind` is `text`, `link` or `image`). Errors with a message are RFC 7807 problem responses.

### Authentication

`POST /api/auth/login` with the password returns a device token. Send it on every other request as `Authorization: Bearer <token>`, including the WebSocket upgrade request. A token stays valid until the device logs out or is revoked; it is not tied to the password (see [Changing the password](#changing-the-password)).

The web UI uses a session cookie instead. Cookie-authenticated `POST`/`DELETE` calls must also send the antiforgery token in a `RequestVerificationToken` header; bearer-authenticated calls don't need it.

### Endpoints

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/health` | none | Health check. |
| `POST` | `/api/auth/login` | none | Body `{password, deviceName, platform}` (device name 1-50 chars). Returns `{deviceId, token}`. 401 on a wrong password, 429 when rate-limited or too many logins are being checked at once. |
| `POST` | `/api/auth/logout` | bearer | Revokes the calling device's token and closes its WebSockets. 204. |
| `GET` | `/api/devices` | bearer | Lists devices: `[{id, name, platform, createdAt, lastSeenAt, isCurrent}]`. |
| `DELETE` | `/api/devices/{id}` | bearer | Revokes a device and closes its WebSockets. 204, or 404. |
| `POST` | `/api/clips/text` | bearer | Body `{text, copiedAt?, backfill}`. Returns the clip. 400 if empty, 413 if over `PP_MAX_TEXT_KB`. |
| `POST` | `/api/clips/image` | bearer | `multipart/form-data` (see below). Returns the clip. 400 for an invalid image or body, 413 if over `PP_MAX_IMAGE_MB` or 50 megapixels. |
| `GET` | `/api/clips?q=&cursor=&limit=` | bearer | History, newest first (`lastCopiedAt` desc). `q` searches text; `limit` defaults to 50 (max 200). Returns `{items, nextCursor}`; pass `nextCursor` back as `cursor` for the next page. |
| `GET` | `/api/clips/{id}` | bearer | One clip, or 404. |
| `GET` | `/api/clips/{id}/image` | bearer | The full image as `image/png`. |
| `GET` | `/api/clips/{id}/thumbnail` | bearer | A PNG thumbnail that fits in 256x256. |
| `POST` | `/api/clips/{id}/restore` | bearer | Treats the clip as freshly copied by the caller: bumps it to the top and pushes it live. Returns the clip, or 404. |
| `DELETE` | `/api/clips/{id}` | bearer | Deletes a clip (and its image). 204, or 404. |
| `GET` | `/api/sync?afterSeq=&limit=` | bearer | Changes after a sequence number, in `seq` order: `{clips, deletedIds, latestSeq, hasMore}`. `limit` defaults to 200 (max 500). |
| `GET` | `/ws` | bearer | WebSocket for live updates (see below). |

A clip is `{id, seq, kind, text, hash, width, height, sizeBytes, sourceDeviceId, sourceDeviceName, lastCopiedAt}`. `text` is null for images; `width`/`height` are null for text. Every change (new clip, dedup bump, restore, delete) gets a new, higher `seq`.

Uploading content that already exists (same `hash`) does not create a duplicate: the existing clip is bumped to the top and returned.

### Image upload

`POST /api/clips/image` takes `multipart/form-data` with these parts, in any order:

- `file` (required): the image (PNG, JPEG, WebP, ...). It is normalized to PNG, rotated upright per its EXIF orientation. Images over 50 megapixels are rejected with 413.
- `copiedAt` (optional): ISO 8601 time the image was copied. Only used with `backfill`.
- `backfill` (optional): `true` for a clip queued while offline.

Live uploads always get the server's time. Backfilled uploads keep `copiedAt`, clamped to no later than the server's time, and are never applied to other devices' clipboards. The same applies to `copiedAt`/`backfill` on text uploads.

### WebSocket

Connect to `/ws` with the bearer token in the upgrade request. Messages are JSON text frames:

- `{"type":"clip.created","clip":{...},"live":true}`: a clip was added, bumped or restored. `live` is `false` for backfilled clips.
- `{"type":"clip.deleted","id":"...","seq":123}`: a clip was deleted.
- Send `{"type":"ping"}` and the server answers `{"type":"pong"}`. Other messages are ignored.

The server closes the socket with status 1008 (`revoked`) when the device is revoked or logs out, and with 1001 when it shuts down.

### Client rules

- Ignore `clip.created` messages whose `clip.sourceDeviceId` is your own device id; you already have that clip.
- Only apply `live: true` clips to the clipboard. Backfilled clips go into history only.
- Order by `seq` (or `lastCopiedAt` for display), not by arrival order: broadcasts can arrive out of `seq` order.
- On every (re)connect, catch up with `GET /api/sync?afterSeq=<highest seq you have seen>`, repeating while `hasMore` is true, then continue from `latestSeq`. Apply at most the newest caught-up clip to the clipboard, and only if it is newer than what you last applied.
- Reconnect with backoff after the socket closes, except after a `revoked` close: log in again instead.

### Content hash

`hash` is computed the same way on every client so that echo prevention and dedup work across platforms. The reference implementation is `ContentHash` in `src/PrivatePaste.Protocol/ContentHash.cs`:

- Text: lowercase hex of `SHA-256(UTF-8("t:" + text))`, where `text` has CRLF and lone CR converted to LF (nothing else changes).
- Image: lowercase hex of `SHA-256(UTF-8("i:{width}x{height}:") ++ pixels)`, where `pixels` is the decoded image as RGBA8888, unpremultiplied, row-major, with no row padding.

Ports must reproduce these test vectors:

| Input | Hash |
|---|---|
| text `hello` | `1bec6ebade142b8ea787b2744bbb12d1915e0d0d5e8e31f7f5452911840de3a9` |
| text `a\nb` (and `a\r\nb`) | `b77cc1fcd69063256ffc07884bde76c875b73e56a786925feedd7ba881f0616c` |
| text `šećer 🚀` | `812328f074c3d5a025a329dd4674a676d83d5f926bd79af643a51e783ed3ac39` |
| 1x1 image, RGBA `[255, 0, 0, 255]` | `8d833d22068fdf91eecbd0d310208548d1f9418c6513c572a7c7d01fc696c49a` |

## Local development

```
PP_DB_KEY=0123456789abcdef0123 PP_PASSWORD=dev PP_DATA_DIR=./data dotnet run --project src/PrivatePaste.Server
```

Run the tests with `dotnet test`.

## Desktop app

A tray app that keeps your clipboard in sync with the server (Windows; see [macOS and Linux](#macos-and-linux) for the other platforms).

- **Download** `PrivatePaste-<version>-win-x64.zip` from the latest GitHub Release and unzip it anywhere. It is a single self-contained `PrivatePaste.exe`; no installer or .NET runtime is needed.
- **SmartScreen**: the build is not code-signed, so Windows shows "Windows protected your PC". Click "More info", then "Run anyway".
- **First sign-in**: start the app, enter the server URL (`https://` only; plain `http://` is accepted for localhost), the server password and a device name. After that it stays signed in and starts minimized in the tray.
- **Hotkey**: Ctrl+Shift+V opens the history popup. Pick an item to re-sync it, put it on the clipboard and paste it. The hotkey can be changed in Settings.
- **Pause**: the tray menu has Pause sync. While paused nothing is sent or received, and clips copied during the pause are never uploaded, not even after Resume.
- **Where data lives**: `%APPDATA%\PrivatePaste` holds the settings, the encrypted clip cache (latest 500 items), the DPAPI-protected sign-in token and `logs\`.
- **Uninstall**: if you are signed in, untick "Start with Windows" in Settings; if you are signed out (Settings needs a sign-in), remove the autostart value as described in the next point instead. Then quit the app from the tray and delete `PrivatePaste.exe` and the `%APPDATA%\PrivatePaste` folder.
- **Leftover autostart entry**: if you deleted the exe without unticking "Start with Windows", remove the value `PrivatePaste` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (regedit, or `reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v PrivatePaste /f`).
- **Build it yourself**: `powershell -File scripts/publish-windows.ps1` writes `artifacts/win-x64/PrivatePaste.exe` and `artifacts/PrivatePaste-win-x64.zip`. Pushing a `desktop-v*` tag runs `.github/workflows/desktop.yml`, which tests, builds all platforms and attaches them to a GitHub Release.

## macOS and Linux

The desktop app also runs on macOS and Linux. These ports are best-effort: they are built in CI but have had less real-world testing than Windows.

**macOS** (Apple Silicon and Intel, `PrivatePaste-<version>-osx-arm64.zip` / `osx-x64.zip`)

- The app is not signed or notarized. Unzip it, then right-click `PrivatePaste.app` and choose Open, or run `xattr -d com.apple.quarantine PrivatePaste.app`.
- Grant Accessibility access (System Settings > Privacy & Security > Accessibility) so the global hotkey (Cmd+Shift+V) and paste work. After an update macOS can drop the grant because the app is unsigned; remove and re-add it if the hotkey stops working.
- The sign-in token is encrypted with a random key kept in your login keychain (service `PrivatePaste`). The key is handed to the `security` tool on stdin, so it is not visible in the process list. While the app reads it, other programs run by your user can ask the keychain for it too (macOS prompts).
- Autostart is a LaunchAgent at `~/Library/LaunchAgents/xyz.ficdev.privatepaste.plist`.

**Linux** (`PrivatePaste-<version>-linux-x64.tar.gz`)

- Unpack it and run `./install.sh` for a launcher in your application menu, or just run `PrivatePaste/PrivatePaste`.
- An X11 session works best. On Wayland the clipboard and global hotkey are limited by the compositor; the app tells you once.
- The sign-in token key is stored through the Secret Service. Install `secret-tool` (package `libsecret-tools` on Debian/Ubuntu, `libsecret` on Fedora/Arch) and have a keyring running (GNOME Keyring, KWallet's Secret Service). Without it the app falls back to a key file with mode 600 in `~/.config/privatepaste/` and says so once; that guards against other users, not other programs of yours. If the keyring is locked or unavailable at start, the app shows the sign-in screen and writes the error to its log (`~/.config/privatepaste/logs/`); unlock the keyring and restart it to pick up your sign-in and history. Starting never replaces a key it cannot reach; signing in again does (that sign-in starts a fresh history anyway). macOS behaves the same when the keychain is locked.
- Autostart is `~/.config/autostart/privatepaste.desktop`.

## Android app

A sideloaded Android app (Android 10 or newer) that puts clips from your other devices on the phone's clipboard, sends the phone's clipboard or a shared item to them, and shows the searchable history. It talks only to your server: no Google Play, no push service, no analytics.

- **Install**: download `PrivatePaste-android-<version>.apk` from the latest GitHub Release on the phone, open it, and allow "Install unknown apps" for the browser or file manager you used when Android asks.
- **Sign in**: enter the server URL (`https://` only; plain `http://` works only for localhost, for development), the server password and a device name. After that the app stays signed in.
- **Allow notifications and background use**: after the first sign-in the app asks for the notification permission (the service shows a permanent "PrivatePaste connected" notification) and to be exempt from battery optimization. Allow both. Without them Android stops the connection when the phone sleeps.
- **Send clipboard tile**: pull down Quick Settings, tap the edit (pencil) button, and drag "Send clipboard" into the active tiles. Tapping it sends what is on the clipboard now.
- **Share to PrivatePaste**: in any app, use Share and pick "Send to PrivatePaste" to send text, a link or an image.
- **History**: the app lists the latest 200 clips with search. Tap a clip to put it on the clipboard, long-press to delete it (it disappears on your other devices too).
- **Delivery** (Settings): "Copy automatically" writes incoming clips straight to the clipboard and is the default. Android may refuse clipboard writes from a background app, mostly while the phone is locked or another app is in front. If clips from your other devices don't land on the clipboard in that case, switch to "Show a notification with a Copy button": each incoming clip posts a notification, and tapping Copy puts it on the clipboard.
- **Battery settings (Xiaomi, Samsung, Huawei and others)**: these phones stop background apps more aggressively than stock Android. Open Settings → Apps → PrivatePaste → Battery and choose "Unrestricted" (the wording differs by maker). Also:
  - **Xiaomi (MIUI / HyperOS)**: set Battery saver to "No restrictions" for the app and turn on Autostart for it in Security or Permissions.
  - **Samsung (One UI)**: remove PrivatePaste from "Sleeping apps" and "Deep sleeping apps" (Settings → Battery → Background usage limits).
  - **Huawei (EMUI / HarmonyOS)**: in the app's Battery settings turn off "Manage automatically" and allow Auto-launch, Secondary launch and Run in background.
- **Revoked device**: if the phone is revoked from the web UI, the app shows a "Signed out" notification and stops. Sign in again to resume.
- **Updating**: install a newer APK over the old one. Android only accepts it when it is signed with the same key as the installed app, so keep using the release APKs from this repository's Releases. The first time you switch between a debug build and a release build you must uninstall the old one.
- **Release** (for the repository owner): run `powershell -File scripts/android-keystore.ps1`. It creates `android/keystore/release.jks` (git-ignored) and prints the password once and the `gh secret set` commands for the four secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`. Run them, then push a tag such as `android-v0.1.0`. `.github/workflows/android.yml` tests, builds the signed APK and attaches it to a GitHub Release (a version with a `-`, like `0.2.0-rc1`, becomes a prerelease). Back up `release.jks`: without it, updates can't be installed over the existing app.
- **Build it yourself**: in `android/`, run `./gradlew assembleDebug`. `./gradlew assembleRelease` signs only when `PP_ANDROID_KEYSTORE` (path), `PP_ANDROID_KEYSTORE_PASSWORD`, `PP_ANDROID_KEY_ALIAS` and `PP_ANDROID_KEY_PASSWORD` are set; otherwise it produces an unsigned APK and warns. `-PppVersion=0.1.0 -PppVersionCode=1` set the version.
- **End-to-end test**: with a local server running (see [Local development](#local-development), `PP_PASSWORD=dev`, e.g. `ASPNETCORE_URLS=http://localhost:5299`), run `PP_E2E_URL=http://localhost:5299 ./gradlew testDebugUnitTest --tests '*LocalServerE2eTest*'` in `android/`. Without `PP_E2E_URL` the test is skipped.
