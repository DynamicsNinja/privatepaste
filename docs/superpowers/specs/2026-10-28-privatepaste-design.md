# PrivatePaste — Design

Date: 2026-10-28
Status: Draft, awaiting approval

## 1. Intent

A personal clipboard sync and history tool that runs on the owner's own server. Copy something on one device and it shows up on the clipboard of your other devices. Everything you've ever copied can be searched and pasted again.

**Why:** The owner mostly moves text, links and screenshots from a Windows PC to an Android phone, and sometimes between two Windows PCs. Today that means emailing or messaging things to themselves. They don't want a hosted service that keeps everyone's clipboards together. The data should live only on their own server, deployed as a Docker container on their Dokploy instance.

**Success:** Copy on the PC, pick up the phone, and paste. Nothing in between. Missed or slow clips would be noticed and would undermine trust in the tool.

## 2. Goals

1. **Automatic PC → phone sync.** A copy on any desktop lands on the Android clipboard without touching the phone. This is the primary flow.
2. **Automatic PC ↔ PC sync** in both directions.
3. **Phone → PC with one tap:** an Android share-sheet target plus a Quick Settings tile that sends the current clipboard. Android 10+ forbids background clipboard reads, so this cannot be automatic.
4. **Content types:** plain text, links (text that is a URL, shown with a link icon) and images.
5. **History kept forever,** searchable, with click/tap to copy. Items can be deleted individually, and deletes propagate to every device.
6. **Self-hosted:** one Docker container with a volume, deployable on Dokploy behind its HTTPS/domain.
7. **Platforms:** Windows (first-class), macOS, Linux desktop, Android.

## 3. Non-goals (for now)

- Multiple users or accounts. It's single-user. The design shouldn't block adding users later, but nothing is built for it.
- Files other than images, and rich text/HTML formatting. Only plain text is kept.
- iOS.
- Play Store distribution. The Android app is a sideloaded APK.
- Treating sensitive content specially. **The owner decided passwords and one-time codes are synced and stored like anything else.**

## 4. Anti-goals (would be failure even if it "works")

- Any third-party service in the data path, including Firebase/FCM push, cloud relays or analytics.
- Clips that silently go missing or arrive out of order, or a sync loop where a clip bounces between devices forever.
- Clipboard contents stored in plaintext on the server's disk.
- A server that someone on the internet can use before the owner has secured it.

## 5. Decisions made by the owner

| Topic | Decision |
|---|---|
| Direction & mode | Automatic PC→phone and PC↔PC; phone→PC by explicit tap |
| Content | Text, links, images |
| Privacy meaning | Own server, own data, no shared multi-tenant service |
| Hosting | Docker on the owner's Dokploy |
| Users | Just the owner |
| Retention | Forever, with manual delete |
| Passwords/OTPs | Same as any other clip |
| Android | Permanent notification is acceptable in exchange for instant delivery |
| Desktop history UI | **Quick popup (option A)**: global hotkey opens a small searchable list near the cursor; Enter pastes into the previously focused app |
| Tech stack | Delegated to Claude (see §6) |
| Android fallback | If silent background `setPrimaryClip` fails on the owner's phone, a per-clip notification with a **Copy** action (one tap) is acceptable |
| Restoring from history | Picking an old item from history on any device **does** re-sync it: it bumps to the top and replaces the other devices' clipboards like a fresh copy |
| Code signing | No Apple Developer account, no Windows code-signing certificate. Ship unsigned |

## 6. Architecture (Claude's calls unless marked otherwise)

```
 ┌───────────── Windows / macOS / Linux ─────────────┐        ┌──────── Android ────────┐
 │ Tray app (C#/.NET, Avalonia)                       │        │ Kotlin app              │
 │  • clipboard watcher  • hotkey popup  • local cache│        │  • foreground service   │
 └───────────────┬────────────────────────────────────┘        │  • share target + tile  │
                 │  HTTPS: REST + WebSocket                    │  • history screen       │
                 ▼                                             └───────────┬─────────────┘
        ┌────────────────────── Docker container ──────────────────────────┐
        │ ASP.NET Core server                                               │
        │  • REST API  • WebSocket push  • minimal web UI                   │
        │  • SQLCipher DB (metadata + text + FTS)  • encrypted image files  │
        │  volume: /data                                                    │
        └───────────────────────────────────────────────────────────────────┘
                 ▲ TLS terminated by Dokploy (Traefik)
```

### 6.1 Server: C# / ASP.NET Core, one container

- Chosen to match the owner's existing .NET skills.
- **Storage:** SQLite encrypted with SQLCipher (via `SQLitePCLRaw.bundle_e_sqlcipher`). It holds clip metadata, text content and an FTS5 index for search. Images are stored as AES-256-GCM-encrypted files under `/data/blobs/`, with thumbnails stored the same way. Everything persistent lives in the `/data` volume.
- **Encryption key:** from the env var `PP_DB_KEY`. Without it the server refuses to start. The README must warn that losing the key means losing the history, and that backups of `/data` are useless without it. The goal is protecting the volume, backups and disk images. It does not protect against someone with root on a running server, and this is accepted.
- **Real-time push:** plain WebSocket endpoint (`/ws`) with a small JSON message protocol (`clip.created`, `clip.deleted`, `ping`). Bulk data goes over REST. Plain WebSockets were chosen over SignalR to keep the Kotlin client dependency-free.
- **Web UI:** a minimal server-rendered page to log in, browse/search history, copy or delete items, and list/revoke devices. It's a fallback and admin tool, not a primary client.

### 6.2 Desktop app: C# / Avalonia, one codebase for Win/macOS/Linux

- Runs in the system tray, starts with the OS (autostart setting, on by default), and needs no main window.
- **Clipboard watcher:**
  - Windows: `AddClipboardFormatListener` (event-driven).
  - macOS: poll `NSPasteboard.changeCount` (~500 ms).
  - Linux: X11 is supported. Wayland is best-effort, because clipboard watching there is restricted. Document the limitation instead of fighting it.
- **Hotkey popup (owner's choice A):** the default is **Ctrl+Shift+V** (Cmd+Shift+V on macOS), configurable. It opens a small borderless window near the mouse cursor with a search box focused. Typing filters the list, arrow keys move the selection, and Enter puts the item on the clipboard, closes the popup and sends a paste keystroke to the previously focused window. Esc closes it. Clicking an item does the same as Enter. Each row shows a type icon, a preview (first line of text, or an image thumbnail), the source device name and relative time.
  - macOS needs the Accessibility permission to send the paste keystroke. If it isn't granted, fall back to copy-only and show a one-time hint.
- **Tray menu:** Pause sync / Resume, Open history (same popup), Settings (server URL, hotkey, autostart, device name), Sign out, Quit.
- **Local cache:** the most recent 500 items (text + thumbnails) are kept on disk so the popup opens instantly and works offline. The cache file is encrypted with OS facilities (DPAPI on Windows, Keychain-stored key on macOS, libsecret on Linux). Searches beyond the cache go to the server's search endpoint. Results are merged, with cached hits first.
- **Offline:** clips copied while the server is unreachable are queued locally and uploaded in order on reconnect, using the client's original copy time, flagged as backfill (see §6.5). *(Claude's call.)*
- **Pause sync:** stops both sending and receiving. Clips copied while paused are never uploaded, not even after Resume. Pausing exists so things don't leave the machine. The tray icon shows a paused state. *(Claude's call.)*

### 6.3 Android app: native Kotlin

- **Min SDK 29** (Android 10). Distributed as a signed APK from the project's releases.
- **Foreground service** with a permanent, low-priority notification ("PrivatePaste connected") holds the WebSocket. It uses the `specialUse` foreground-service type, because `dataSync` is limited to 6 h/day on Android 15+. On first run the app asks for an exemption from battery optimization. Reconnect uses exponential backoff, and the app listens for network changes.
- **Receiving:** on `clip.created` from another device, call `ClipboardManager.setPrimaryClip`. **Fallback (owner-approved):** if the §7 spike shows that background writes are blocked on the owner's device, post a heads-up notification per clip with a preview and a **Copy** action instead. Make this a setting (auto / notification), so both paths exist if behavior varies by Android version. Images are fetched, cached in app storage, and placed on the clipboard as a `content://` URI through a `FileProvider`.
- **Sending (one tap):**
  - A share-sheet target accepts text, URLs and images from any app.
  - A Quick Settings tile reads the clipboard by briefly bringing a transparent activity to the foreground, sends it, and closes.
- **History screen:** a full-screen list with a search bar on top. Tap copies the item, long-press offers delete. Images show as thumbnails. It uses the same server search endpoint and a small local cache of recent items.

### 6.4 Auth & device pairing

- **Password:** set with the env var `PP_PASSWORD` (stored hashed with Argon2id at startup). Because the password comes from the environment, there is no unprotected "first visitor sets the password" window on an internet-facing server. To change it, change the env var and restart.
- **Device sign-in:** each device signs in once with server URL + password and names itself (e.g. "PC-work", "Phone"). The server issues a long-lived random device token, which the server stores hashed and the device stores in OS secure storage (Credential Manager / Keychain / libsecret / Android Keystore). All REST and WS calls use `Authorization: Bearer <token>`.
- Devices can be revoked from the web UI. A revoked token is rejected immediately and its live socket is closed.
- Rate-limit password attempts (for example 5/min per IP).
- HTTPS is required. Clients refuse `http://` URLs except `localhost` (for development).

### 6.5 Sync rules

- **Clip identity:** each clip gets a server-assigned ID and a SHA-256 of its normalized content.
- **Echo prevention:** when a client writes a remote clip to its own clipboard, it records that hash. If the watcher then sees the same hash it doesn't upload it again. This prevents ping-pong between devices.
- **Restore from history (owner's decision):** picking an item in the popup, the Android history or the web UI is treated as a fresh copy. It is uploaded, deduped, bumped to the top, and replaces the other devices' clipboards. No separate suppress path is needed. Echo prevention still applies on the receiving side.
- **Dedup:** if an uploaded clip's hash matches an existing clip, the server doesn't create a duplicate. It bumps the existing item to the top (updates `last_copied_at` and source device) and broadcasts that.
- **Ordering:** history is sorted by `last_copied_at` descending. Live clips use server time. Backfilled clips (queued offline) use the client's copy time, clamped to no later than server time.
- **Delivery:** the newest live clip from another device replaces the receiving device's clipboard. Backfilled clips go into history only and never overwrite another device's clipboard. When a device reconnects and catches up on missed clips, they go into history, and only the newest one is applied, and only if it is newer than what the device last applied. *(Claude's call.)*
- **Limits:** text up to 1 MB and images up to 20 MB. Larger items aren't synced, and the desktop shows a tray notification instead. Images are normalized to PNG on the server, and a thumbnail (~256 px) is generated.
- **Link detection:** text that is a single URL (http/https) is typed as `link`. This only affects the icon and display. It is still stored as text.
- **Latency target:** under 2 s from copy on PC to clipboard on phone on a normal connection.

### 6.6 Deployment

- One Docker image (multi-stage .NET build) that listens on port 8080 and uses the volume `/data`.
- Env vars: `PP_DB_KEY` (required), `PP_PASSWORD` (required), and optionally `PP_MAX_IMAGE_MB` and `PP_MAX_TEXT_KB`.
- Ship a `docker-compose.yml` that Dokploy can use directly. Dokploy's Traefik handles the domain and TLS, and WebSocket upgrade must work through it.
- `GET /health` for Dokploy health checks.

## 7. Risks to check early

1. **Android background clipboard write + persistent socket** on recent Android (14/15/16) with OEM battery killers: build a throwaway spike first to confirm that `setPrimaryClip` from the foreground service works while the screen is off or another app is open, and that the connection survives for hours.
2. **Global hotkey + paste injection on macOS and Linux** from Avalonia, which may need small native shims per OS.
3. **SQLCipher + FTS5** working together in the chosen SQLitePCLRaw bundle inside the Linux container.

## 8. Left to the builder

- Project/solution layout, library choices within the stack above (for example image processing or the hotkey library), and the exact REST/WS schemas.
- Desktop installers and packaging per OS, as long as Windows gets an easy install with autostart. **Everything ships unsigned** (owner decision). Document the SmartScreen "Run anyway" step on Windows, and the right-click → Open / `xattr -d com.apple.quarantine` step on macOS. On macOS, keep the bundle ID and install path stable so the Accessibility grant survives updates as far as possible, and document re-granting it if it's lost.
- Visual styling of the popup, Android screens and web UI. Keep it clean and compact. The popup should feel like Win+V.
- Testing strategy and CI.
- Order of delivery, suggested as server → Windows desktop → Android → macOS/Linux, since the primary flow is Windows→Android.
