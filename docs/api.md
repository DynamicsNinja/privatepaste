# API reference

The HTTP and WebSocket API the apps use. Everything here is enough to write your own client.

This is protocol 2: clips are end-to-end encrypted, and the server stores and serves only ciphertext (see [End-to-end encryption](#end-to-end-encryption)). A client encrypts before it uploads and decrypts after it downloads; the server checks only what it can see: the kind, the keyed hash, the key id and the sizes.

All JSON is camelCase; enums are lowercase strings (`kind` is `text`, `link` or `image`, and later releases add more). Errors with a message are RFC 7807 problem responses. Binary values in JSON are standard base64 with padding, unless a field says otherwise.

## Protocol version

Send `X-PrivatePaste-Protocol: 2` on every request, including the WebSocket upgrade.

- A request with a bearer token to `/api/clips…`, `/api/sync`, `/api/history…`, `/api/vault…` or `/ws` without that header gets **426** with detail `Update PrivatePaste to keep syncing.`, so an app from before end-to-end encryption stops syncing instead of uploading plaintext or showing ciphertext.
- `/api/auth/*` (sign-in and sign-out), `/api/server-info`, `/api/me`, `/api/devices`, `/api/settings`, `/health` and the web pages aren't gated, so an old app can still sign out cleanly.
- The web UI's own requests use the session cookie and pass the gate; its scripts send the header anyway.

Read `GET /api/server-info` at sign-in and on every reconnect, before any vault or clip call. A server from before protocol 2 doesn't have it (404): tell the user `Update your PrivatePaste server to keep syncing.` and don't sync.

## Authentication

`POST /api/auth/login` with a person's name and password returns a device token for that person. Send it on every other request as `Authorization: Bearer <token>`, including the WebSocket upgrade request. A token stays valid until the device logs out or is revoked; it is not tied to the password, so changing your own password keeps your devices signed in. When the owner resets a person's password or removes them, though, all that person's tokens are revoked (see [Changing the password](self-hosting.md#changing-the-password)).

Everyone signed in shares one history, and so one vault key. A person other than the owner only sees and revokes their own devices, and clearing the history, changing settings, setting up encryption, changing the passphrase and resetting encryption are the owner's alone (marked "owner" below).

The web UI uses a session cookie instead. Cookie-authenticated `POST`/`PUT`/`DELETE` calls must also send the antiforgery token in a `RequestVerificationToken` header; bearer-authenticated calls don't need it.

## Endpoints

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/health` | none | Health check. |
| `GET` | `/api/server-info` | none | `{"protocol":2,"kinds":["text","link","image"],"limits":{"textKb":<PP_MAX_TEXT_KB>,"imageMb":<PP_MAX_IMAGE_MB>}}`. Only create kinds listed in `kinds`, and use `limits` for every size check and size message. |
| `POST` | `/api/auth/login` | none | Body `{username, password, deviceName, platform}` (device name 1-50 chars; `username` is matched case-insensitively). Returns `{deviceId, token}`. 400 with detail `Update PrivatePaste to sign in.` when `username` is missing or blank (apps from before there were people), 401 with detail `Wrong name or password.` for an unknown name or a wrong password, 429 when rate-limited or too many logins are being checked at once. |
| `POST` | `/api/auth/logout` | bearer | Revokes the calling device's token and closes its WebSockets. 204. |
| `GET` | `/api/me` | bearer | `{name, isOwner}` for the person signed in. |
| `GET` | `/api/devices` | bearer | Lists devices: `[{id, name, platform, createdAt, lastSeenAt, isCurrent}]`. The owner gets every person's devices; anyone else only their own. |
| `DELETE` | `/api/devices/{id}` | bearer | Revokes a device and closes its WebSockets. 204, or 404 when there is no such device or it belongs to someone else (the owner can revoke anyone's). |
| `GET` | `/api/vault` | bearer | The passphrase wrap of the vault key, `{keyId, kdf, nonce, wrappedKey}` (see [Vault wrap](#vault-wrap)), or 404 when encryption isn't set up yet. |
| `PUT` | `/api/vault` | bearer, owner | Body: a wrap. Creates the vault when there is none (201), or replaces the wrap when its `keyId` is the current one, as when the passphrase changes (200). 409 with detail `Encryption is already set up with a different key.` otherwise; 400 for a wrap that fails the checks in [Vault wrap](#vault-wrap). Returns the wrap. |
| `POST` | `/api/vault/reset` | bearer, owner | Body: a wrap for a new vault key. Stores it and deletes every clip (pinned ones too), every tombstone, every blob and the last-cleared time, then sends `vault.reset` on every WebSocket. Returns the wrap; 400 for an invalid wrap. Meant for a lost passphrase. |
| `POST` | `/api/vault/handovers` | bearer | Body `{blob}`: a [handover](#adding-a-device-by-qr-code), canonical base64 of 1 to 1024 bytes. Returns `{id, expiresAt}`; it can be fetched once within 10 minutes. 400 when `blob` is missing, empty or not canonical base64, 413 when it is over 1024 bytes. |
| `GET` | `/api/vault/handovers/{id}` | bearer | `{blob}`, deleting it in the same step. 404 once it was fetched or after it expired. |
| `POST` | `/api/clips` | bearer | Uploads an encrypted clip: `multipart/form-data`, see [Uploading a clip](#uploading-a-clip). Returns the clip. |
| `GET` | `/api/clips?cursor=&limit=&pinned=` | bearer | History, newest first (`lastCopiedAt` desc). `limit` defaults to 50 (max 200). With `pinned=true`, only pinned clips, most recently pinned first. Returns `{items, nextCursor}`; pass `nextCursor` back as `cursor` for the next page. 400 with detail `Search runs on your devices` when `q` is given: the server can't search ciphertext (see [Search](#search)). |
| `GET` | `/api/clips/{id}` | bearer | One clip, or 404. |
| `GET` | `/api/clips/{id}/thumb` | bearer | The clip's encrypted thumbnail (`application/octet-stream`), or 404 when it has none (`hasThumb` is false). |
| `GET` | `/api/clips/{id}/blobs/{n}` | bearer | The clip's encrypted blob `n`, counting from 0 (`application/octet-stream`); for an image, blob 0 is the full picture. 404 when `n` isn't below `blobCount`. |
| `POST` | `/api/clips/{id}/restore` | bearer | Treats the clip as freshly copied by the caller: bumps it to the top and pushes it live. Returns the clip, or 404. |
| `POST` | `/api/clips/{id}/pin` | bearer | Pins a clip: it survives clearing and auto-delete. Doesn't change `lastCopiedAt`. Returns the clip, or 404. Pinning a pinned clip changes nothing. |
| `DELETE` | `/api/clips/{id}/pin` | bearer | Unpins a clip. Returns the clip, or 404. |
| `DELETE` | `/api/clips/{id}` | bearer | Deletes a clip (and its thumbnail and blobs), pinned or not. 204, or 404. |
| `POST` | `/api/history/clear` | bearer, owner | Deletes every unpinned clip copied (or unpinned) before now, with their files, and remembers that time. Returns `{deleted}`, the count. 403 for anyone but the owner. |
| `GET` | `/api/settings` | bearer | Returns `{autoDeleteDays}`: `null` (keep everything), `7`, `30` or `90`. |
| `PUT` | `/api/settings` | bearer, owner | Body `{autoDeleteDays}` as above. Returns the saved settings; 400 for any other value, 403 for anyone but the owner. |
| `GET` | `/api/sync?afterSeq=&limit=` | bearer | Changes after a sequence number, in `seq` order: `{clips, deletedIds, latestSeq, hasMore}`. `limit` defaults to 200 (max 500). |
| `GET` | `/ws` | bearer | WebSocket for live updates (see below). Device tokens only. |

Everything marked bearer also accepts the web UI's session cookie, except `/ws`. Owner-only calls answer 403 to anyone else. The upload endpoints from before protocol 2 (`POST /api/clips/text`, `POST /api/clips/image`) and the server-decoded `GET /api/clips/{id}/image` and `/thumbnail` are gone.

A clip is `{id, seq, kind, text, hash, width, height, sizeBytes, sourceDeviceId, sourceDeviceName, sourceDevicePlatform, lastCopiedAt, pinnedAt, sourceUserName, payload, keyId, hasThumb, blobCount}`:

- `payload` is the clip's content, sealed (see [The clip payload](#the-clip-payload)); `keyId` is the vault key that sealed it.
- `hasThumb` says whether `GET /api/clips/{id}/thumb` has a thumbnail, and `blobCount` how many blobs `GET /api/clips/{id}/blobs/{n}` has (1 for an image, 0 for text and links).
- `text`, `width` and `height` are always null: the text and the image size are inside the payload. They stay in the JSON so the DTO only ever grows.
- `hash` is the keyed hash (see [Keyed hash](#keyed-hash)), and `sizeBytes` the plaintext size the uploader declared.
- `pinnedAt` is always present: the time the clip was pinned, or null when it isn't pinned.
- `sourceDevice*` describe the device that last copied the clip (a new upload, a dedup bump or a restore). `sourceDevicePlatform` is that device's login `platform`, lowercased (`windows`, `macos`, `linux`, `android`, or whatever else a client sent), `web` for the web UI (which has no `sourceDeviceId`), or null when unknown. Clients show anything they do not recognize as an unknown device.
- `sourceUserName` is the current name of the person who last copied the clip; it is null while the server has only one person and when unknown. Clients show it whenever it is present.
- Every change (new clip, dedup bump, restore, pin, delete) gets a new, higher `seq`.

Uploading content that already exists (same `hash`) does not create a duplicate: the existing clip is bumped to the top and returned, and the thumbnail and blobs sent with the upload replace the stored ones.

## Uploading a clip

`POST /api/clips` takes `multipart/form-data` with these parts, in any order. Each part is read into memory up to its own limit and answered with 413 as soon as it goes over; other part names are ignored.

- `meta` (required): JSON `{kind, keyId, hash, payload, sizeBytes, copiedAt?, backfill}`.
  - `kind`: `text`, `link` or `image`; anything else is 400.
  - `keyId`: the vault key the clip is sealed with. Without a vault the upload is 409 with detail `Set up encryption first.`; when it isn't the current vault's `keyId`, 409 with title `Encryption was reset` and detail `Encryption was reset. Enter the new passphrase.`
  - `hash`: the keyed hash, 64 lowercase hex characters.
  - `payload`: the sealed payload, base64.
  - `sizeBytes`: the plaintext size: the UTF-8 byte length of the text, or the byte length of the image's PNG. Not negative.
  - `copiedAt` (optional): ISO 8601 time the clip was copied. Only used with `backfill`.
  - `backfill`: `true` for a clip queued while offline.
- `thumb` (optional, images only): the sealed thumbnail, at most 1 MiB (1048576 bytes).
- `blob0`, `blob1`, ... : the encrypted blobs, numbered from 0 without gaps. An image has exactly one, `blob0`, the encrypted PNG.

Limits, with `PP_MAX_TEXT_KB` and `PP_MAX_IMAGE_MB` (as `limits` in server info) in bytes:

- **Text and link:** no thumbnail and no blobs (400 otherwise). The declared `sizeBytes` may be at most `PP_MAX_TEXT_KB` × 1024, which is the limit users see; the sealed payload may be up to 6 × that + 4096 bytes, because the payload's JSON can escape one UTF-8 byte into six (`\uXXXX`). Over either is 413 with detail `Text exceeds the <n> byte limit.`
- **Image:** the blob may be at most `PP_MAX_IMAGE_MB` × 1 MiB plus the blob header (9 bytes) and a 16-byte tag per 1 MiB chunk; over is 413. The sealed payload (which holds only the size) may be at most 4096 bytes. Clients check the plaintext before uploading: images over 50 megapixels or over `imageMb` are refused on the device, since the server can't decode them.
- The whole request is capped at the larger of the two cases plus some framing.

Other answers: 400 for a body that isn't multipart, a missing or unreadable `meta`, a payload that isn't base64, a repeated part or a gap in the blob numbers; 409 with detail `That content was already stored as a different kind.` when the same `hash` exists under another kind.

Live uploads always get the server's time. Backfilled uploads keep `copiedAt`, clamped to no later than the server's time, and are never applied to other devices' clipboards.

A backfilled upload is refused with `410` (problem title `Cleared`) and nothing is stored when its `copiedAt` is before the last history clear, or older than the auto-delete age. Live uploads are never refused this way.

## WebSocket

Connect to `/ws` with the bearer token and the protocol header in the upgrade request. Messages are JSON text frames:

- `{"type":"clip.created","clip":{...},"live":true}`: a clip was added, bumped or restored. `live` is `false` for backfilled clips, for pin changes (the clip arrives again with the new `pinnedAt`) and when clips are re-sent because a person's name changed or people were added or removed (the clip arrives again with the new `sourceUserName`).
- `{"type":"clip.deleted","id":"...","seq":123}`: a clip was deleted.
- `{"type":"vault.reset"}`: the owner reset encryption. Every clip is gone and the vault has a new key (see the client rules).
- Send `{"type":"ping"}` and the server answers `{"type":"pong"}`. Other messages are ignored.

The server closes the socket with status 1008 (`revoked`) when the device is revoked or logs out, and with 1001 when it shuts down.

## Client rules

- Send `X-PrivatePaste-Protocol: 2` on every request. Read `/api/server-info` at sign-in and on every reconnect; a 404 there means the server is too old (`Update your PrivatePaste server to keep syncing.`). A 426 from the server means this app is too old (`Update PrivatePaste to keep syncing.`).
- Only create kinds the server lists in `kinds`, and check sizes against its `limits`.
- **Never apply a clip of a kind you don't know.** Parse an unknown `kind` without failing (keep it as "unknown"), show it as `Update PrivatePaste to see this clip`, and never put it on the clipboard. Later releases add kinds within protocol 2, and this keeps them compatible both ways. Ignore unknown fields in the payload, too.
- Before syncing, compare the `keyId` of your stored vault key with `GET /api/vault`. When it differs (or the vault is gone), or `vault.reset` arrives, or an upload gets 409 titled `Encryption was reset`: delete the stored key, clear the local history cache, thumbnails and upload queue, and ask for the new passphrase (`Encryption was reset. Enter the new passphrase.`). Do the same the first time you meet an encrypted server with a cache from before, so no plaintext lingers on the device.
- Without a vault (404 on `GET /api/vault`), the owner sets it up (`/api/me` says `isOwner`); anyone else is told `Ask the owner to set up encryption.` and doesn't sync.
- Signing out, or being revoked, deletes the stored vault key along with the token.
- Never apply a `clip.created` message whose `clip.sourceDeviceId` is your own device id to the clipboard; you copied it. Still replace your stored copy of the clip with it: a pin or a re-send changes the clip without a new copy.
- Only apply `live: true` clips to the clipboard. Backfilled clips go into history only.
- Order by `seq` (or `lastCopiedAt` for display), not by arrival order: broadcasts can arrive out of `seq` order.
- On every (re)connect, catch up with `GET /api/sync?afterSeq=<highest seq you have seen>`, repeating while `hasMore` is true, then continue from `latestSeq`. Apply at most the newest caught-up clip to the clipboard, and only if it is newer than what you last applied.
- A pin or unpin, or a re-sent clip with a changed `sourceUserName`, arrives as `clip.created` with `live: false` and a new `seq`; replace your copy of the clip. Its `lastCopiedAt` is unchanged.
- Treat `410` on a queued (backfilled) upload as "drop it quietly": the history was cleared or the clip is past the auto-delete age. Treat 409 titled `Encryption was reset` on a queued upload the same way, after relocking.
- Reconnect with backoff after the socket closes, except after a `revoked` close: log in again instead.
- Never log passphrases, the vault key or its subkeys, one-time handover keys, QR text or decrypted content.

## Search

The server only holds ciphertext, so it can't search. Apps search the clips they keep locally, and when those run out they offer **Search older clips**: page through `GET /api/clips`, decrypt each payload and match it on the device, showing matches as they come.

## End-to-end encryption

The server stores clips it can't read. It still sees who copied a clip, from which device and platform, and when; the clip's kind, pin state and order; and its exact sizes (`sizeBytes` and the ciphertext lengths). It never sees content or thumbnails. At-rest encryption with `PP_DB_KEY` stays and hides that metadata from someone who takes the disk without the server's configuration.

The reference implementation is in `src/PrivatePaste.Protocol/Crypto/`; the browser port is `src/PrivatePaste.Server/wwwroot/js/e2e.js` and the Android one `core/crypto/VaultCrypto.kt` under `android/app/src/main/java/xyz/ficdev/privatepaste/`.

### Keys

- **Vault key (VK):** 32 random bytes, created once per server by the owner device or web session that sets up encryption. Everyone on the server shares it. It never reaches the server unwrapped.
- **Subkeys**, from HKDF-SHA256 over VK with no salt:
  - `encKey` = HKDF(VK, info `privatepaste v1 enc`, 32 bytes): the AES-256-GCM key for all content.
  - `hashKey` = HKDF(VK, info `privatepaste v1 hash`, 32 bytes): the key of the keyed hash.
  - `keyId` = the first 8 bytes of HKDF(VK, info `privatepaste v1 key id`, 32 bytes), as 16 lowercase hex characters. It names the key without revealing it.
- Changing the passphrase re-wraps the same VK, so `keyId` stays and no clip is re-encrypted. Only Reset encryption makes a new VK.

### Vault wrap

The passphrase is separate from the login password, which the server sees at every sign-in.

- `kek` = Argon2id(passphrase as UTF-8 after NFC normalization, a 16-byte random `salt`, memory 64 MiB (65536 KiB), 3 iterations, parallelism 1, 32 bytes of output).
- `wrappedKey` = AES-256-GCM(`kek`, a 12-byte random `nonce`, VK, associated data `privatepaste v1 vault ` + `keyId`): 32 bytes of ciphertext and a 16-byte tag.
- On the wire: `{"keyId":"…","kdf":{"alg":"argon2id","memoryKiB":65536,"iterations":3,"parallelism":1,"salt":"<base64>"},"nonce":"<base64>","wrappedKey":"<base64>"}`.
- The server accepts `keyId` as 16 lowercase hex characters, `alg` `argon2id`, `memoryKiB` 65536 to 262144, `iterations` 3 to 10, `parallelism` 1 to 8, and a 16-byte `salt`, 12-byte `nonce` and 48-byte `wrappedKey` in canonical base64; clients refuse to unwrap anything outside the same bounds. The parameters travel with the wrap so later versions can raise them.
- To unlock, fetch the wrap, run Argon2id with its parameters and open `wrappedKey`. A wrong passphrase fails the GCM check: show `That passphrase doesn't unlock this server's history.` Then derive `keyId` from the key and check that it matches.
- A new passphrase must have at least 12 characters and not be in the bundled list of common passphrases (`wwwroot/js/common-passphrases.txt`, the same file on every platform). Apps show `Use at least 12 characters. A short sentence works well.`

### Small items

A clip's payload and its thumbnail are sealed as one small item each: `0x01 || nonce (12 bytes) || ciphertext || tag (16 bytes)`, AES-256-GCM under `encKey` with a random nonce and associated data `privatepaste v1 ` + purpose. Purpose is `payload` or `thumb` (`blob` is reserved), so one kind of item can't be swapped in for another.

### Blobs

A full image is a blob, encrypted in chunks so neither side has to hold it twice:

- A 9-byte header `0x02 || noncePrefix (8 random bytes)`, then chunks of 1 MiB (1048576 bytes) of plaintext, the last one shorter or equal.
- Chunk `i` is `ciphertext || tag`: AES-256-GCM under `encKey` with nonce `noncePrefix || uint32 big-endian i` and associated data `privatepaste v1 blob || uint32 big-endian i || isFinal byte` (1 on the last chunk, 0 on the others). The final flag stops truncation.
- An empty input is one final chunk with an empty plaintext.

### The clip payload

A UTF-8 JSON object (camelCase), sealed as a `payload` small item and sent as base64:

- Text and link: `{"text":"…"}`.
- Image: `{"width":1600,"height":900}`. The picture itself is blob 0: a PNG, upright (EXIF orientation applied), at most 50 megapixels. The thumbnail is a PNG that fits 256x256, sealed as a `thumb` small item.

Readers ignore fields they don't know: later releases add more.

### Keyed hash

The `hash` the server sees is lowercase hex of HMAC-SHA256(`hashKey`, UTF-8 of the [content hash](#content-hash) in lowercase hex). The same content still dedups and bumps, but without the key the server can't check a clip against a guess. The content hash itself is unchanged and stays the local hash for echo prevention.

### Adding a device by QR code

A device that has the key (Add a device in the desktop, Android or web Settings) can hand it to a phone without the passphrase:

1. It draws a one-time key `otk` (32 random bytes) and seals VK under it as a small item with associated data `privatepaste v1 handover`: `0x01 || nonce || ciphertext || tag`, 61 bytes.
2. It uploads that with `POST /api/vault/handovers` and shows a QR code with the text `privatepaste:handover?id=<id>&k=<otk as base64url, no padding>&kid=<keyId>`.
3. The new device, already signed in, scans it, takes the blob with `GET /api/vault/handovers/{id}`, opens it with `otk` and checks that the key inside derives `kid`.

The server never sees `otk`. A code works once and only for 10 minutes, so a photo of it is useless afterwards. Parsers accept only the canonical spelling (a UUID `id`, 43 base64url characters for `k`, 16 lowercase hex for `kid`), reject a repeated field and ignore unknown ones.

### Test vectors

[`e2e-test-vectors.json`](e2e-test-vectors.json) holds the vectors every client must reproduce: the subkeys and `keyId` of a fixed VK, Argon2id outputs (including the NFC and NFD spellings of the same passphrase), a passphrase wrap, a sealed payload and thumbnail, a two-chunk blob and an empty one, a keyed hash and two handovers with their QR text. They are written by `tests/PrivatePaste.Tests/Protocol/VaultCryptoTests.cs` (with `PP_WRITE_VECTORS=1`) and checked by the .NET tests, the Android JVM tests and the browser module's Node test (`tests/web/e2e.test.mjs`).

## Content hash

The content hash is computed the same way on every client so that echo prevention and dedup work across platforms; the server sees it only through the [keyed hash](#keyed-hash). The reference implementation is `ContentHash` in `src/PrivatePaste.Protocol/ContentHash.cs`:

- Text: lowercase hex of `SHA-256(UTF-8("t:" + text))`, where `text` has CRLF and lone CR converted to LF (nothing else changes).
- Image: lowercase hex of `SHA-256(UTF-8("i:{width}x{height}:") ++ pixels)`, where `pixels` is the decoded image as RGBA8888, unpremultiplied, row-major, with no row padding.

Ports must reproduce these test vectors:

| Input | Hash |
|---|---|
| text `hello` | `1bec6ebade142b8ea787b2744bbb12d1915e0d0d5e8e31f7f5452911840de3a9` |
| text `a\nb` (and `a\r\nb`) | `b77cc1fcd69063256ffc07884bde76c875b73e56a786925feedd7ba881f0616c` |
| text `šećer 🚀` | `812328f074c3d5a025a329dd4674a676d83d5f926bd79af643a51e783ed3ac39` |
| 1x1 image, RGBA `[255, 0, 0, 255]` | `8d833d22068fdf91eecbd0d310208548d1f9418c6513c572a7c7d01fc696c49a` |
