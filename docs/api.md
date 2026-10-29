# API reference

The HTTP and WebSocket API the apps use. Everything here is enough to write your own client.

All JSON is camelCase; enums are lowercase strings (`kind` is `text`, `link` or `image`). Errors with a message are RFC 7807 problem responses.

## Authentication

`POST /api/auth/login` with the password returns a device token. Send it on every other request as `Authorization: Bearer <token>`, including the WebSocket upgrade request. A token stays valid until the device logs out or is revoked; it is not tied to the password (see [Changing the password](self-hosting.md#changing-the-password)).

The web UI uses a session cookie instead. Cookie-authenticated `POST`/`DELETE` calls must also send the antiforgery token in a `RequestVerificationToken` header; bearer-authenticated calls don't need it.

## Endpoints

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

A clip is `{id, seq, kind, text, hash, width, height, sizeBytes, sourceDeviceId, sourceDeviceName, sourceDevicePlatform, lastCopiedAt}`. `text` is null for images; `width`/`height` are null for text. `sourceDevice*` describe the device that last copied the clip (a new upload, a dedup bump or a restore). `sourceDevicePlatform` is that device's login `platform`, lowercased (`windows`, `macos`, `linux`, `android`, or whatever else a client sent), `web` for the web UI (which has no `sourceDeviceId`), or null when unknown. Clients show anything they do not recognize as an unknown device. Every change (new clip, dedup bump, restore, delete) gets a new, higher `seq`.

Uploading content that already exists (same `hash`) does not create a duplicate: the existing clip is bumped to the top and returned.

## Image upload

`POST /api/clips/image` takes `multipart/form-data` with these parts, in any order:

- `file` (required): the image (PNG, JPEG, WebP, ...). It is normalized to PNG, rotated upright per its EXIF orientation. Images over 50 megapixels are rejected with 413.
- `copiedAt` (optional): ISO 8601 time the image was copied. Only used with `backfill`.
- `backfill` (optional): `true` for a clip queued while offline.

Live uploads always get the server's time. Backfilled uploads keep `copiedAt`, clamped to no later than the server's time, and are never applied to other devices' clipboards. The same applies to `copiedAt`/`backfill` on text uploads.

## WebSocket

Connect to `/ws` with the bearer token in the upgrade request. Messages are JSON text frames:

- `{"type":"clip.created","clip":{...},"live":true}`: a clip was added, bumped or restored. `live` is `false` for backfilled clips.
- `{"type":"clip.deleted","id":"...","seq":123}`: a clip was deleted.
- Send `{"type":"ping"}` and the server answers `{"type":"pong"}`. Other messages are ignored.

The server closes the socket with status 1008 (`revoked`) when the device is revoked or logs out, and with 1001 when it shuts down.

## Client rules

- Ignore `clip.created` messages whose `clip.sourceDeviceId` is your own device id; you already have that clip.
- Only apply `live: true` clips to the clipboard. Backfilled clips go into history only.
- Order by `seq` (or `lastCopiedAt` for display), not by arrival order: broadcasts can arrive out of `seq` order.
- On every (re)connect, catch up with `GET /api/sync?afterSeq=<highest seq you have seen>`, repeating while `hasMore` is true, then continue from `latestSeq`. Apply at most the newest caught-up clip to the clipboard, and only if it is newer than what you last applied.
- Reconnect with backoff after the socket closes, except after a `revoked` close: log in again instead.

## Content hash

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
