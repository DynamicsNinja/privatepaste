# PrivatePaste

A self-hosted clipboard sync server. Text and images copied on one device show up on your others. Everything is stored encrypted at rest (SQLite3 Multiple Ciphers database keyed by `PP_DB_KEY`, blobs encrypted with an HKDF-derived key), and no third-party network services are used. History is kept until you delete items.

The API, at a high level: log in with the password to get a device token, then use the token for the clips endpoints (create, list, delete) and for the WebSocket at `/ws`, which pushes live updates. A web UI is served at `/`, and `/health` reports server health.

## Deploy on Dokploy

1. Create a **Compose** application from this repository (it uses `docker-compose.yml`).
2. Set the environment variables:
   - `PP_DB_KEY` (required, at least 16 characters): encryption key for the database and blobs.
   - `PP_PASSWORD` (required): the login password.
   - `PP_MAX_IMAGE_MB` (optional, default 20) and `PP_MAX_TEXT_KB` (optional, default 1024).
3. Add a domain pointing to service `privatepaste`, port **8080**, with HTTPS enabled. Traefik passes WebSockets through by default.
4. Optionally point Dokploy's health check at `/health`. The image has no `curl`, so there is no built-in `HEALTHCHECK`.

Data lives in the named volume `privatepaste-data`, mounted at `/data`.

## Generating keys

```
openssl rand -base64 32
```

Use the output for `PP_DB_KEY` (and a strong, separate value for `PP_PASSWORD`).

**Back up `PP_DB_KEY`. Without it the data is unrecoverable.**

## Changing the password

Edit `PP_PASSWORD` in the environment and redeploy. The password is hashed with Argon2id at startup. Existing device tokens keep working.

## Backups

Copy the `privatepaste-data` volume (the `/data` directory) and store it together with `PP_DB_KEY` (keep the key separate from where you expose the backup, but do not lose it).

## Proxy and forwarded headers

Login is rate-limited to 5 attempts per minute per client IP. The server trusts `X-Forwarded-For` from the reverse proxy to identify the client IP, so only expose the container through the proxy (Traefik). Do not publish port 8080 directly to the internet, or clients could spoof their IP and bypass the limit.

## Local development

```
PP_DB_KEY=0123456789abcdef0123 PP_PASSWORD=dev PP_DATA_DIR=./data dotnet run --project src/PrivatePaste.Server
```

Run the tests with `dotnet test`.
