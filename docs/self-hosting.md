# Self-hosting

Running the PrivatePaste server: deployment, configuration, keys, backups and local development.

## Deploy on Dokploy

1. Create a **Compose** application from this repository (it uses `docker-compose.yml`).
2. Set the environment variables:
   - `PP_DB_KEY` (required, at least 16 characters): encryption key for the database and blobs. It must be a high-entropy random value (see [Generating keys](#generating-keys)), not a memorable passphrase.
   - `PP_PASSWORD` (required): the owner's password. It creates the owner (named `owner`) on first start; changing it later resets the owner's password, see [Changing the password](#changing-the-password).
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

## End-to-end encryption

Clips are encrypted on the devices before they reach the server, with a vault key that is shared by everyone on the server and unlocked with a passphrase. The server stores the vault key only wrapped with that passphrase (Argon2id), so neither the server nor someone who steals it, its configuration and backups can read a clip. Details of the formats are in the [API reference](api.md#end-to-end-encryption).

- **Setting it up**: on a new server, the owner's first app or web session that opens the history asks to **Set up encryption**. Choose a passphrase twice: at least 12 characters, not a common one ("Use at least 12 characters. A short sentence works well."). It is separate from the login password. Other people's devices show "Ask the owner to set up encryption." until then, and don't sync.
- **Unlocking a device**: each device asks for the passphrase once after signing in. Android can instead scan a code from **Add a device** (in the Settings of the desktop app, the Android app or the web UI, on a device that is already unlocked); the code works once, for 10 minutes. The web UI keeps the key only for the browser tab and has a **Lock** button next to Sign out.
- **Changing the passphrase**: the owner, in Settings on any unlocked device or in the web UI. The history stays and unlocked devices keep working; only devices that unlock later need the new one.
- **What the server still sees**: who copied each clip, from which device and platform, and when; its kind, exact size and pin state. Never the content or thumbnails.
- **Search** runs on the devices, which hold the key. The history screens search the clips each device keeps and offer **Search older clips** for the rest.
- **The web UI** decrypts in the browser with code your server sends. Someone who steals the server and keeps it running could serve altered page code to any browser that is unlocked, or unlocks, while it runs. An unlocked tab is exposed as soon as it loads another page, so use **Lock** (or close the tab) when you're done. The apps don't have this risk.

### Why `PP_DB_KEY` still matters

`PP_DB_KEY` encrypts the database and stored files at rest, as before. The clip contents are already unreadable without the passphrase, but the metadata above (people, devices, times, kinds, sizes, pins) is only hidden by `PP_DB_KEY`, from someone who gets the disk or a backup without the server's configuration. Keep it, and keep backing it up apart from the data.

### Reset encryption

**A lost passphrase can't be recovered**, by you or anyone else: the history is encrypted with a key only it unlocks. The way out is **Settings > Reset encryption** in the web UI (owner only). After a confirmation, it deletes the whole history, pinned clips included, on every device, and sets up encryption again with a new passphrase you choose there.

Every device then shows "Encryption was reset. Enter the new passphrase.", clears its local copy of the history and its queue of unsent clips, and unlocks with the new passphrase.

## People

A server has people, who share one history. Each signs in with a name and a password. The owner is the person created on first start, named `owner` (rename it from the **People** page), with `PP_PASSWORD` as the password. There is exactly one owner.

The owner manages people on the **People** page of the web UI (History, People, Settings in the top bar):

- **Add person**: a name (1-32 characters, unique, not case-sensitive) and a first password (at least 8 characters). Hand them over yourself; there is no self sign-up, invite or email.
- **Rename**: clips show the person's current name everywhere.
- **Reset password**: sets a new password and signs out all that person's devices and web sessions.
- **Remove**: signs out all their devices and web sessions at once. Their past clips stay in the history under their name. The name can be reused later.
- **Sign out** a single device, for any person.

Anyone changes their own password under **Settings > Change password** (it signs them out of their other browsers; their devices stay signed in) and signs out their own devices on the People page. Only the owner sees auto-delete and Clear history, and the server refuses them to anyone else. History rows show a name chip once the server has more than one person.

## Changing the password

- **Your own password** (owner or anyone): **Settings > Change password** in the web UI. This signs you out of every other browser; the browser you changed it in and your devices stay signed in. Attempts count against the sign-in limit (5 per minute per IP).
- **Forgot the owner's password**: set a new `PP_PASSWORD` and redeploy. When the server starts with a different `PP_PASSWORD` than last time (it remembers a hash, never the value), it resets the owner's password to it. This signs the owner out of every browser, but not out of their devices. If `PP_PASSWORD` is unchanged, a password the owner set on the web stays in force.
- **Another person's password**: the owner resets it on the People page.
- If a password leaked, reset it: the person's devices are signed out. Passwords are hashed with Argon2id.

## Backups

1. Get a consistent copy of `/data` (the `privatepaste-data` volume): either stop the container first (`docker compose stop`, copy, `docker compose start`), or take a snapshot of the volume. Copying the files of a running server can catch the database mid-write.
2. Store `PP_DB_KEY` separately and safely (for example in a password manager), not next to the backup. The backup is encrypted with it: **without `PP_DB_KEY` a backup is useless**, and anyone who has both the backup and the key can read the metadata (people, devices, times, sizes).
3. The clips in a backup are end-to-end encrypted: after a restore, devices unlock them with the passphrase that was in use when the backup was taken.

## Clearing history and auto-delete

Clips are kept until someone deletes them. The **Settings** page of the web UI has two ways to clean up, both for every device and for the owner only:

- **Clear history** deletes every clip that is not pinned, after a confirmation that says how many clips go. Devices that were offline remove them when they reconnect.
- **Auto-delete history** (Off, or after 7, 30 or 90 days; off by default) deletes unpinned clips once they are that old. The server checks at startup and then every hour. A clip's age counts from the later of its last copy and its unpin.

Pinned clips are never cleared or auto-deleted. Cleared stays cleared: a device that was offline during a clear and uploads old copies later is told "Cleared" (HTTP 410, see the [API](api.md)) and the copies are dropped, and the same goes for copies older than the auto-delete age.

## Proxy and forwarded headers

Sign-in (and Change password in Settings) is rate-limited to 5 attempts per minute per client IP (IPv6 clients per /64 prefix). The server trusts `X-Forwarded-For` from the reverse proxy to identify the client IP, so only expose the container through the proxy (Traefik). Do not publish port 8080 directly to the internet, or clients could spoof their IP and bypass the limit.

## Local development

```
PP_DB_KEY=0123456789abcdef0123 PP_PASSWORD=dev PP_DATA_DIR=./data dotnet run --project src/PrivatePaste.Server
```

Sign in as `owner` with the password `dev`, then set up encryption with any passphrase of 12 characters or more. To start over, stop the server and delete the data directory.

Run the tests with `dotnet test`.
