# Self-hosting

Running the PrivatePaste server: deployment, configuration, keys, backups and local development.

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

## Clearing history and auto-delete

Clips are kept until someone deletes them. The **Settings** page of the web UI has two ways to clean up, both for every device:

- **Clear history** deletes every clip that is not pinned, after a confirmation that says how many clips go. Devices that were offline remove them when they reconnect.
- **Auto-delete history** (Off, or after 7, 30 or 90 days; off by default) deletes unpinned clips once they are that old. The server checks at startup and then every hour. A clip's age counts from the later of its last copy and its unpin.

Pinned clips are never cleared or auto-deleted. Cleared stays cleared: a device that was offline during a clear and uploads old copies later is told "Cleared" (HTTP 410, see the [API](api.md)) and the copies are dropped, and the same goes for copies older than the auto-delete age. This is a change to the database, so back up `/data` before upgrading to the version that adds it.

## Proxy and forwarded headers

Login is rate-limited to 5 attempts per minute per client IP (IPv6 clients per /64 prefix). The server trusts `X-Forwarded-For` from the reverse proxy to identify the client IP, so only expose the container through the proxy (Traefik). Do not publish port 8080 directly to the internet, or clients could spoof their IP and bypass the limit.

## Local development

```
PP_DB_KEY=0123456789abcdef0123 PP_PASSWORD=dev PP_DATA_DIR=./data dotnet run --project src/PrivatePaste.Server
```

Run the tests with `dotnet test`.
