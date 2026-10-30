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

- **Your own password** (owner or anyone): **Settings > Change password** in the web UI. This signs you out of every other browser; the browser you changed it in and your devices stay signed in. When the owner does this, web sessions from before the upgrade to people end too. Attempts count against the sign-in limit (5 per minute per IP).
- **Forgot the owner's password**: set a new `PP_PASSWORD` and redeploy. When the server starts with a different `PP_PASSWORD` than last time (it remembers a hash, never the value), it resets the owner's password to it. This signs the owner out of every browser, including web sessions from before the upgrade to people, but not out of their devices. If `PP_PASSWORD` is unchanged, a password the owner set on the web stays in force.
- **Another person's password**: the owner resets it on the People page.
- If a password leaked, reset it: the person's devices are signed out. Passwords are hashed with Argon2id.

## Upgrading to multiple people

**Back up `/data` before upgrading**: the first start migrates the database.

- **If the server was shared, change the owner's password first**, right after upgrading: **Settings > Change password**, or set a new `PP_PASSWORD` and restart. Everyone who knew the old shared password could sign in on the web as `owner`; changing it ends those web sessions. The owner's devices stay signed in.
- The owner account is created from your existing setup (name `owner`, password `PP_PASSWORD`). Every existing device becomes the owner's and keeps working without signing in again; clips copied so far are the owner's.
- On a server that several people already shared, the devices that belong to someone else are not moved. The owner signs them out on the People page; each person then signs in again with their own account, which the owner adds there.
- Apps from before this version can't sign in. The server answers them with "Update PrivatePaste to sign in.", which the old Android app shows; the old desktop app shows "Can't reach the server." instead. Update them (release the server first, then the desktop and Android apps). Devices already signed in with an old app keep syncing.
- New apps still sign in to an old server.

## Backups

1. Get a consistent copy of `/data` (the `privatepaste-data` volume): either stop the container first (`docker compose stop`, copy, `docker compose start`), or take a snapshot of the volume. Copying the files of a running server can catch the database mid-write.
2. Store `PP_DB_KEY` separately and safely (for example in a password manager), not next to the backup. The backup is encrypted with it: **without `PP_DB_KEY` a backup is useless**, and anyone who has both the backup and the key can read everything.

## Clearing history and auto-delete

Clips are kept until someone deletes them. The **Settings** page of the web UI has two ways to clean up, both for every device and for the owner only:

- **Clear history** deletes every clip that is not pinned, after a confirmation that says how many clips go. Devices that were offline remove them when they reconnect.
- **Auto-delete history** (Off, or after 7, 30 or 90 days; off by default) deletes unpinned clips once they are that old. The server checks at startup and then every hour. A clip's age counts from the later of its last copy and its unpin.

Pinned clips are never cleared or auto-deleted. Cleared stays cleared: a device that was offline during a clear and uploads old copies later is told "Cleared" (HTTP 410, see the [API](api.md)) and the copies are dropped, and the same goes for copies older than the auto-delete age. This is a change to the database, so back up `/data` before upgrading to the version that adds it.

## Proxy and forwarded headers

Sign-in (and Change password in Settings) is rate-limited to 5 attempts per minute per client IP (IPv6 clients per /64 prefix). The server trusts `X-Forwarded-For` from the reverse proxy to identify the client IP, so only expose the container through the proxy (Traefik). Do not publish port 8080 directly to the internet, or clients could spoof their IP and bypass the limit.

## Local development

```
PP_DB_KEY=0123456789abcdef0123 PP_PASSWORD=dev PP_DATA_DIR=./data dotnet run --project src/PrivatePaste.Server
```

Sign in as `owner` with the password `dev`.

Run the tests with `dotnet test`.
