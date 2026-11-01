# End-to-end encryption, rich text and files

Status: draft for the owner's review (2026-11-01). Builds on `2026-10-28-privatepaste-design.md`, `2026-10-29-privacy-pins-history-design.md` and `2026-10-30-multiple-users-design.md`, and on the server, desktop and Android apps and the web UI as they are on `main` today.

Three features, built and released in this order, each as one coordinated server, desktop and Android release:

1. **End-to-end encryption (E2E)** for the clip types that exist today (text, link, image).
2. **Rich text**: formatted text keeps its formatting.
3. **Files**: copied files sync and can be pasted, saved or shared.

E2E comes first because it decides how the server stores everything. Rich text and files are then encrypted from day one.

## Why

- **E2E.** The owner's threat is a **stolen server**: someone who gets the machine, its disk or its backups. Today that someone usually gets everything. `PP_DB_KEY` sits in the same environment (Dokploy, `.env`) as the data it protects, so at-rest encryption only helps when the disk leaves without its configuration. Competitors in the self-hosted space (ClipCascade, CrossPaste) offer E2E, and for a product named PrivatePaste it is the first question privacy-minded users ask.
- **Rich text.** The owner usually copies formatted code (VS Code, JetBrains IDEs) and wants the colours to survive to Word, Outlook, Teams or Gmail on another device, while a terminal still gets plain text. It should work for any formatted source.
- **Files.** Small files (documents, screenshots saved as files, configs) should appear on the other device, and be pastable, savable and shareable there.

## Today (for the builder)

- **Protocol** (`src/PrivatePaste.Protocol/Dtos.cs`): `ClipDto` carries `kind` (`text`, `link`, `image`), plaintext `text`, `hash`, `width`/`height` and `sizeBytes`, plus device, person, time and pin metadata. `ClipKind` is a C# enum serialized by `JsonStringEnumConverter`; Android mirrors it in `api/Protocol.kt`.
- **Hash** (`ContentHash`): SHA-256 of the normalized text, or of the RGBA pixels. The server column `clips.hash` is `UNIQUE` and drives dedup: the same content bumps the existing clip. Test vectors are in `docs/api.md` and must pass on .NET and Android.
- **Server**:
  - stores text in SQLite (SQLite3 Multiple Ciphers, key `PP_DB_KEY`) with an FTS5 trigram index for `GET /api/clips?q=`;
  - decodes uploaded images, applies EXIF orientation, normalizes them to PNG, makes 256 px thumbnails, and stores both as AES-GCM files under `/data/blobs/` (`BlobStore`).
- **Web UI** (`Pages/Index.cshtml`) renders history on the server with Razor, including the text of each clip, and searches with `q`. `wwwroot/site.js` handles Copy, Pin and Delete.
- **Desktop** (`PrivatePaste.Client.Core`): `ClipboardCoordinator` watches the clipboard, `SyncEngine` uploads and catches up, `HistoryCache` keeps about 500 recent clips encrypted on disk (`DpapiSecretProtector` / `AesGcmSecretProtector`), and `ThumbnailFetcher` downloads thumbnails. `BlockedApps` and `Platform/PrivateCopyMarkers.cs` skip private copies.
- **Android** (`xyz.ficdev.privatepaste`): `sync/SyncEngine`, `storage/HistoryCache` (about 200 clips, encrypted with `KeyProvider` / `CryptoFile`), `send/ShareActivity` (text, links, images), and `service/ServiceClipApplier`, which writes incoming clips to the clipboard or posts a notification with Copy.

## Goals

### 1. End-to-end encryption

#### Threat and promise

- **Protects against:** anyone who obtains the server's disk, database, blob files or backups, **including** its configuration and `PP_DB_KEY`. They must not be able to read any clip content: text, images, rich text, file names or file contents.
- **Doesn't protect against:**
  - people who share the server: they hold the same key by design;
  - an attacker who runs the stolen server live and serves modified web UI code to a signed-in browser (see [Web UI](#web-ui));
  - traffic analysis of the metadata below.
- **The server still sees (owner's acceptance):**
  - who copied, from which device and platform, and when;
  - the clip's kind (`text`, `link`, `image`, `rich`, `file`), pin state and order;
  - its size, which is exact (Claude's call): `sizeBytes`, ciphertext lengths and, for file clips, the number of files. Padding to hide sizes isn't worth its cost against a stolen server.

  It never sees content, file names or thumbnails.
- At-rest encryption with `PP_DB_KEY` **stays**. It still hides the metadata from someone who takes the disk without the configuration.

#### Always on, and the upgrade starts blank (owner's decisions)

- E2E is **not a setting**. A server on this version stores only end-to-end encrypted clips.
- **Upgrading deletes the existing history, pins included.** The server migration drops every clip, tombstone, blob and FTS row, and resets the "last cleared" mark. Release notes and the README tell owners to back up `/data` first (CLAUDE.md requires asking before a schema change reaches the live server).
- Apps clear their local history caches, thumbnails and outbound queues when they first meet an E2E server, so no plaintext from before lingers on devices either.
- **Older apps stop syncing until updated (owner's decision).** This deliberately overrides the "compatible both ways" protocol rule. An old app can't read encrypted clips, and accepting its plaintext uploads would defeat E2E. See [Protocol version gate](#protocol-version-gate).

#### Keys

- **Vault key (VK):** 32 random bytes from a CSPRNG, created once per server by the first device that sets up encryption. It never reaches the server unwrapped.
- **Subkeys** come from HKDF-SHA256 over VK, with no salt and a fixed `info` per purpose:
  - `encKey` = HKDF(VK, info `"privatepaste v1 enc"`, 32 bytes): AES-256-GCM key for all content.
  - `hashKey` = HKDF(VK, info `"privatepaste v1 hash"`, 32 bytes): keyed dedup hash.
  - `keyId` = first 8 bytes of HKDF(VK, info `"privatepaste v1 key id"`, 32 bytes), as lowercase hex. It lets every client and the server tell which vault key a clip, wrap or device belongs to, without revealing the key.
- **One vault key per server, shared by everyone on it.** People share one history, so they share one key.
- **No key rotation in this round.** Changing the passphrase re-wraps the same VK. Removing a person signs out their devices (as today), but they still know the key; that matters only together with a stolen server, which is out of scope. Rotation is a candidate for later.

#### Passphrase

- **Separate from the login password (owner's call).** The server sees the login password on every sign-in, so it must never be able to unlock the key.
- **Wrapping:** `kek` = Argon2id(passphrase as UTF-8 after NFC normalization, `salt` 16 random bytes, memory 64 MiB, iterations 3, parallelism 1, output 32 bytes). VK is then encrypted with AES-256-GCM under `kek`, using a random 12-byte nonce and associated data `"privatepaste v1 vault " + keyId`. The parameters are stored with the wrap, so later versions can raise them.
- **Strength:** at least 12 characters, and not in a small bundled list of the most common passphrases. Apps show this rule up front ("Use at least 12 characters. A short sentence works well.").
- **Stored on the server**, wrapped: `{keyId, kdf: {alg: "argon2id", memoryKiB, iterations, parallelism, salt}, nonce, wrappedKey}`. A thief must brute-force Argon2id per guess; the length rule is the main defence.

#### Who sets it up and changes it

- **Setup:** the first **owner** device, or the owner's web session, that meets an E2E server with no vault shows "Set up encryption": choose a passphrase twice. It creates VK, uploads the wrap, and keeps VK locally.
- A **non-owner** device that meets a server with no vault shows "Ask the owner to set up encryption" and doesn't sync.
- **Change passphrase:** owner only, from any owner device or web session that has the key. It re-wraps VK under a new salt and nonce and replaces the server's wrap. `keyId` doesn't change, and no clip is re-encrypted.
- **Reset encryption:** owner only, in the web UI Settings, behind a confirmation. It is for a forgotten passphrase.
  - It deletes the whole history, pins included, then sets up a new vault exactly as above.
  - Every device whose stored `keyId` no longer matches the server's shows "Encryption was reset. Enter the new passphrase." and clears its cache and queue.

#### Getting the key onto a device (owner's decision: both ways)

After the usual sign-in (name, password, device name), a device with no key shows **Unlock** with two choices:

1. **Enter passphrase.** The device fetches the wrap, runs Argon2id, unwraps, and checks the `keyId`. A wrong passphrase fails the GCM check and shows "That passphrase doesn't unlock this server's history."
2. **Scan a code.** Shown where a camera is available: Android, and webcam-equipped laptops only if cheap to do. Otherwise the passphrase is the path.
   - **On a device that already has the key** (desktop Settings, Android Settings, web Settings): "Add a device". It creates a one-time key `otk` (32 random bytes), encrypts VK under `otk` with AES-256-GCM (associated data `"privatepaste v1 handover"`), and uploads the result to `POST /api/vault/handovers`.
   - The server stores it for **10 minutes, single use**, and returns an id. The device then shows a QR code with `privatepaste:handover?id=<id>&k=<base64url otk>&kid=<keyId>`.
   - **On the new device:** it scans the code and calls `GET /api/vault/handovers/{id}`, which returns the blob and deletes it. It decrypts, checks `keyId`, and is ready.
   - The server never sees `otk`. A photo of the QR code is useless after it is used or expires. The scanning device must already be signed in, so a stranger can't fetch the blob.

#### Local key storage

- **Desktop:** VK is stored with the existing `ISecretProtector` (DPAPI on Windows, AES-GCM with a Keychain or libsecret key on macOS and Linux), next to the device token.
- **Android:** VK is stored with the existing `KeyProvider` (Android Keystore-wrapped).
- **Web UI:** see [Web UI](#web-ui).
- **Signing out** a device, or the device being revoked, deletes its VK along with its token.

#### Content encryption

- **Small items** (a clip's payload, thumbnails): AES-256-GCM under `encKey`, with a random 12-byte nonce. The stored or sent form is `version byte 0x01 || nonce || ciphertext || tag`. The associated data is `"privatepaste v1 " + purpose`, where purpose is `payload`, `thumb` or `blob`, so one kind of item can't be swapped in for another.
- **Large items** (full images and files): chunked so neither side holds a whole 25 MB file in memory twice.
  - Chunks are 1 MiB of plaintext (the last may be shorter). The header is `0x02 || 8-byte random noncePrefix`.
  - Each chunk is AES-256-GCM with nonce `noncePrefix || uint32 big-endian chunkIndex`, and associated data `"privatepaste v1 blob" || uint32 chunkIndex || byte isFinal`.
  - The final-chunk flag stops truncation.
  - An empty file is one final chunk with no plaintext.
- **Clip payload:** a UTF-8 JSON object, encrypted as one small item. Version 1 fields, all optional by kind:
  - `text`: string. The plain text for text, link and rich clips.
  - `html`, `rtf`: strings. Rich clips only.
  - `width`, `height`: integers. Images.
  - `files`: `[{name, size, mime}]` in blob order. File clips only.

  Clients ignore unknown payload fields.
- **Cross-platform test vectors** go in `docs/api.md`, as they do for `ContentHash`:
  - HKDF outputs and `keyId` for a fixed VK;
  - Argon2id output for a fixed passphrase and salt;
  - one small-item ciphertext and one two-chunk blob for fixed keys and nonces;
  - the keyed hash.

  .NET, Android (JVM tests) and the browser (a small test page or Node test of the same JS module) must all reproduce them.

#### Dedup and the hash

- `ContentHash` stays as it is for local use (echo guard, "already on the clipboard" checks). Rich and file variants are added (see below).
- **The hash the server sees** is `HMAC-SHA256(hashKey, ContentHash)` as lowercase hex. Identical content still dedups and bumps, but the server can't compare clips against known content without the key.
- `clips.hash` stays `UNIQUE`. The `hash` field on the wire carries the keyed hash.

#### Search moves to the devices

- The server no longer searches. `q` on `GET /api/clips` returns 400 with "Search runs on your devices", and the FTS table and its triggers are dropped.
- Each app searches its local cache instantly: about 500 clips on desktop and 200 on Android today, keeping today's sizes. Search matches the payload's `text`; for files, the file names.
- When local results run out, the history screens offer **"Search older clips"**. It pages through `GET /api/clips` (payloads only, no blobs), decrypts and filters locally, and shows matches as they come. The builder chooses whether those pages are kept in the cache or discarded after the search.

#### Images under E2E

The server can't decode images any more, so the work moves to the clients, which already decode images to compute `ContentHash`:

- Clients upload images as PNG, upright (EXIF orientation applied), at most 50 megapixels and at most `PP_MAX_IMAGE_MB`.
- Clients generate the thumbnail: PNG fitting 256x256, encrypted as a `thumb` item.
- The server enforces only byte sizes.

#### Web UI

Owner's question answered with Claude's recommendation: **the web UI keeps showing clips**.

- **Why keep it:** with no iOS app it is the only way in from an iPhone or a borrowed computer. Its risk (a thief who runs the stolen server live and serves altered code to a waiting user) needs an active, ongoing attack, not a theft.
- **History rendering moves to the browser.** `Index.cshtml` becomes a shell. `site.js` (or a new module) fetches clips as JSON, decrypts them with WebCrypto, and renders the same rows, tabs, Load more and actions as today. Argon2id runs from a WASM build vendored into `wwwroot` (for example `hash-wasm`), so there are no third-party requests.
- **Unlocking:** the history page shows "Unlock your history" with a passphrase field. A separate signed-in web session can still use People and Settings without unlocking.
- **Key lifetime:** the key is kept in `sessionStorage` (survives reload, gone when the tab or browser closes) and never in `localStorage` or cookies. "Lock" is a button next to Sign out.
- **Strict Content-Security-Policy:** `default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; img-src 'self' blob: data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'`. Inline scripts and handlers must move into files.
- **Copy** keeps today's synchronous `ClipboardItem` trick (`site.js` notes Safari's user-activation rule). It writes the decrypted text, the PNG, or (for rich clips) both `text/html` and `text/plain`.
- **Later candidate, not now:** a server setting that turns clip viewing off in the browser for owners who want zero exposure.

#### Protocol version gate

Self-hosters update apps and servers separately, so both directions need handling.

- **Server info (Claude's call):** `GET /api/server-info`, no authentication, returns `{protocol: 2, kinds: ["text", "link", "image"], limits: {textKb, imageMb}}`.
  - Release 2 adds `"rich"` to `kinds` and `richKb` to `limits`; release 3 adds `"file"` and `fileMb`.
  - Apps read it at sign-in and on every reconnect, before any vault or clip call.
- **New app, old server:** `/api/server-info` is missing (404), so the app shows "Update your PrivatePaste server to keep syncing" and doesn't sync. This is how the app tells "old server" apart from "no vault yet", which `GET /api/vault` returns as 404. No pre-E2E code path is kept in the new apps.
- **Newer app, older E2E server:** the app only creates kinds listed in `kinds`.
  - Without `rich`, formatted copies are sent as plain `text` or `link`.
  - Without `file`, file copies aren't sent and the app says "Update your PrivatePaste server to sync files."
  - Clients use the server's `limits` for every size check and size message.
  - A server rejects an upload whose `kind` isn't in its own `kinds` with 400.
- **Old app, new server:** new apps send `X-PrivatePaste-Protocol: 2` on every request, including the WebSocket upgrade.
- Without it, the clip, sync and WebSocket endpoints answer **426** with problem detail `Update PrivatePaste to keep syncing.` Auth and logout keep working, so an old app can still sign out cleanly.
- The old upload endpoints (`POST /api/clips/text`, `POST /api/clips/image`) and the server-decrypted `GET /api/clips/{id}/image` and `/thumbnail` are removed.
- **Unknown kinds must be harmless from release 1 on.** Releases 2 and 3 add kinds (`rich`, `file`) within protocol 2. The release-1 apps must already:
  - parse an unknown `kind` without failing (C#: don't deserialize it straight into the enum; Android: a fallback value);
  - show it as "Update PrivatePaste to see this clip";
  - never apply it to the clipboard.

  This keeps releases 2 and 3 compatible both ways, as CLAUDE.md requires.

#### API changes (release 1)

| Method | Path | Auth | What |
|---|---|---|---|
| `GET` | `/api/server-info` | none | `{protocol, kinds, limits}`, see [Protocol version gate](#protocol-version-gate). |
| `GET` | `/api/vault` | bearer or web session | The wrap `{keyId, kdf, nonce, wrappedKey}`, or 404 when no vault exists yet. |
| `PUT` | `/api/vault` | owner | Create the vault (only when none exists), or replace the wrap when `keyId` matches the current one. 409 otherwise. |
| `POST` | `/api/vault/reset` | owner | Body is a new wrap. Deletes all clips, pins, tombstones and blobs, stores the new wrap, and pushes a `vault.reset` WebSocket message so devices relock. |
| `POST` | `/api/vault/handovers` | bearer or web session | Body `{blob}` (base64, at most 1 KB). Returns `{id, expiresAt}`. |
| `GET` | `/api/vault/handovers/{id}` | bearer or web session | Returns `{blob}` once and deletes it; 404 after use or expiry. |
| `POST` | `/api/clips` | bearer or web session | `multipart/form-data`, described below. Replaces the text and image upload endpoints. |
| `GET` | `/api/clips/{id}/thumb` | bearer or web session | The encrypted thumbnail bytes. |
| `GET` | `/api/clips/{id}/blobs/{n}` | bearer or web session | Encrypted blob `n`: the full image, or file `n`. |

- **`POST /api/clips` parts:**
  - `meta` (JSON): `{kind, keyId, hash, payload (base64), sizeBytes, copiedAt?, backfill}`. `sizeBytes` is the plaintext total the client claims; the server also checks the actual upload sizes.
  - Optional `thumb`, and `blob0` … `blobN`.
- **The server rejects** an upload whose `keyId` isn't the current vault's (409, "Encryption was reset") and enforces the size limits per kind. Dedup by `hash` bumps the existing clip as today, and 410 "Cleared" for old backfills stays.
- **`ClipDto` gains** `payload` (base64 ciphertext), `keyId`, `hasThumb` and `blobCount`. `text` is always null and `width`/`height` are null; they now live inside the payload. All new fields are added as optional, so later versions can grow the DTO compatibly.
- **New WebSocket message:** `{"type":"vault.reset"}`.

#### What doesn't change

People, roles, devices, pins, delete, Clear history, auto-delete, restore, sync and catch-up rules, echo prevention by `sourceDeviceId`, live versus backfill, and private-copy skipping all work as today, on metadata the server still has.

### 2. Rich text

- **Kind `rich`.** Created when the clipboard holds HTML or RTF **and** plain text. Text without formatting stays `text` or `link` as today.
- **What syncs:** every version the source offered, among plain text (always), HTML and RTF. On landing, all of them go on the clipboard together, so each target app picks the one it understands, just as a local copy would.
  - **Windows:** `CF_UNICODETEXT`, `HTML Format` (CF_HTML, including its header offsets) and `Rich Text Format`.
  - **macOS:** `public.utf8-plain-text`, `public.html` and `public.rtf`.
  - **Linux:** `text/plain;charset=utf-8` and `text/html` (RTF only if offered).
  - The builder confirms how Avalonia's clipboard API exposes custom formats on each OS.
- **Android:**
  - Receiving: `ClipData.newHtmlText(label, text, html)`.
  - Sending: plain text only in this round. The tile and Share produce `text` or `link` clips as today.
- **Web UI:**
  - Shows a sanitized preview: an allow-list of formatting tags and inline styles, with scripts, event handlers, forms, iframes and remote images removed. It renders in a sandboxed container, using the browser's `Sanitizer` API when present, otherwise a small vendored sanitizer.
  - Copy writes `text/html` and `text/plain`.
- **History rows** in all three UIs: the first line of the plain text, with a new "rich" kind icon (a formatted-text glyph, drawn like the other kind icons in `branding/ui-icons/` and mirrored on each platform per `DESIGN.md`).
- **Size:** the plain text keeps `PP_MAX_TEXT_KB` (1 MB by default). HTML and RTF together may add up to 5 MB, configurable as `PP_MAX_RICH_KB` (default 5120). Over that, the client drops the formatted versions and syncs a plain `text` clip. The copy never fails, only loses formatting.
- **Hash:** `ContentHash.ForRich(text, html, rtf)` = lowercase hex of SHA-256 over the UTF-8 bytes of `"r:" + NormalizeText(text) + "\0" + (html ?? "") + "\0" + (rtf ?? "")`. Test vectors go in `docs/api.md`.
- **Private copies:** the existing marker and blocklist checks run before any format is read.

### 3. Files

- **Kind `file`.** Created when the clipboard holds a file list:
  - **Windows:** `CF_HDROP`, read through Avalonia's file list.
  - **macOS:** `public.file-url`.
  - **Linux:** `text/uri-list` / `x-special/gnome-copied-files`.

  Android creates one through Share.
- **One clip holds every selected file.** Folders in the selection are skipped. If only folders were selected, nothing syncs and the app says "Folders aren't synced."
- **Limit:** total plaintext size up to `PP_MAX_FILE_MB` (new; default 25). Over it, nothing syncs and the app says "Files over 25 MB aren't synced", using the current limit. The server enforces the limit on the summed blob sizes too.
- **Encrypted:** the file names, sizes and MIME types are in the payload; each file is one chunked blob. The server sees only "file clip, about N MB, M blobs".
- **Where files land (owner's decision: all three):**
  - **Clipboard, desktop:**
    - Incoming file clips are downloaded and decrypted as soon as they arrive (live clips only, like any apply).
    - They're written to a per-clip folder under the app's data directory, for example `%LOCALAPPDATA%\PrivatePaste\files\<clipId>\`, with the original names.
    - The file list goes on the clipboard, so Ctrl+V in Explorer, Finder or a file manager pastes them.
    - The folder is deleted when the clip leaves the local cache, or when the files folder passes 500 MB (oldest first).
  - **Clipboard, Android:**
    - Tapping Copy downloads the files, decrypts them into app storage and puts a `content://` URI (via the existing FileProvider) on the clipboard.
    - Several files become several `ClipData` items.
    - Apps that accept pasted files (mostly chat apps and Gmail) take it.
  - **Save:**
    - Desktop: a "Save" action in the popup (Ctrl+S, routed through `PopupKeyRouter` like the other keys; the popup must still never take focus). It writes to the user's Downloads folder, adding " (2)" style suffixes on name clashes, and shows a toast.
    - Android: Save writes to Downloads through MediaStore.
    - Web UI: a download button per file.
  - **Share, Android:** `ACTION_SEND` / `ACTION_SEND_MULTIPLE` with FileProvider URIs.
- **Android downloads only on demand** (Copy, Save, Share), so mobile data isn't spent silently.
  - In "Copy automatically" mode, an incoming file clip isn't written to the clipboard. It posts a notification, "report.pdf from Work laptop", with **Save**.
  - In notification mode it posts the same notification, with Copy and Save.
- **Sending from Android:** the Share target (`send/ShareActivity`) accepts any MIME type, single or multiple.
- **Web UI upload** is not in scope; the web UI only downloads files.
- **Hash:** `ContentHash.ForFiles(files)` = lowercase hex of SHA-256 over UTF-8 `"f:"` followed, for each file in order, by `name + "\0" + size + "\0" + sha256hex(content) + "\n"`. Test vectors go in `docs/api.md`.
- **History rows:** a file icon and the first file's name, with "+N more" for more files, and the total size.
- **Private copies:** the blocklist applies. File lists carry no password-manager markers, so nothing changes there.

## Non-goals

- Rotating the vault key, or per-person keys.
- iOS app (separate project).
- Rich text sent from Android; web UI uploads of any kind.
- Folder sync, files over the configured limit, resumable or background-chunked transfers beyond the chunk format above.
- Keeping or migrating the pre-E2E history.
- Hiding metadata (who, when, kind, size) from the server.
- A server setting to switch off clip viewing in the web UI (later candidate).

## Anti-goals: this fails even if it "works"

- Any clip content, file name or thumbnail reaches the server, a log, a crash report or the server's disk in plaintext, including during upgrade, errors or retries.
- The passphrase or VK is sent to the server in any form other than the Argon2id-wrapped copy, or is logged anywhere. CLAUDE.md's "never log key presses" rule extends to passphrases and keys.
- An old app silently shows wrong or empty clips instead of "Update PrivatePaste".
- A copied file or formatted text breaks plain-text paste: the plain version must always be on the clipboard.
- The Windows popup takes the focus to support Save.

## Releases

- **Release 1 (E2E):**
  - server, desktop and Android, versions kept identical as the owner prefers;
  - schema change plus a history wipe, so per CLAUDE.md ask the owner to back up `/data` before pushing to `main` (pushing `main` deploys the live server);
  - README, `docs/self-hosting.md`, `docs/api.md` (the new endpoints, payload format, test vectors, 426 gate), `docs/desktop.md` and `docs/android.md` updated: unlock, passphrase rules, what the server still sees;
  - the README's Security section's "this is encryption at rest, not end-to-end encryption" line is rewritten;
  - the Dokploy template in `deploy/dokploy/privatepaste/` bumped.
- **Release 2 (rich text)** and **release 3 (files):** additive within protocol 2. README, docs and the hero images are updated if rows change (the CLAUDE.md README rule: run `scripts/readme/build.ps1` and `scripts/make-site-images.py`).
- **Tests** come with every behaviour change:
  - crypto test vectors on .NET, Android and JS;
  - server tests for the gate, vault, handovers, uploads, limits and the upgrade wipe;
  - client tests for unlock, wrong passphrase, key reset, unknown kinds and each format;
  - render and screenshot checks of new UI in both themes.

## Left to the builder

- How Avalonia exposes CF_HTML, RTF and file lists per OS, and any platform helpers needed.
- The common-passphrase list (source and size), and the exact unlock and setup screen layouts, within `DESIGN.md` and its copy rules.
- Whether "Search older clips" results are cached.
- The browser HTML sanitizer choice, when `Sanitizer` is unavailable.
- Which WASM Argon2id build to vendor, and which Android Argon2id library to use (BouncyCastle has one). All three must match the test vectors.
- Whether webcam QR scanning on desktop is worth doing in this round.
