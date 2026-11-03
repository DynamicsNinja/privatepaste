# Homepage

https://privatepaste.ficdev.xyz: a static page on nginx, one container, built from the repository root with
`site/Dockerfile` (Dokploy application "homepage"). How the page and its images are made is in the comments of
`Dockerfile`, `nginx.conf` and `scripts/make-site-images.py`.

## Hosted version sign-up

The "Want a hosted version?" form at the end of the homepage posts to `/notify`. nginx's njs module runs
[`njs/notify.js`](njs/notify.js), which checks the address and the consent box and appends one line per address to
`/data/signups/hosted.jsonl`:

```
{"email":"someone@example.com","at":"2026-11-03T10:20:30.000Z"}
```

- The address is trimmed and lowercased; one already on the list is not added again (the visitor still sees
  "Thanks", so the form never tells anyone who has signed up).
- Nothing else is kept: no IP address, no user agent. `/notify` is left out of the access log, and rate-limit
  refusals are logged below the error log's level.
- A hidden field (`website`) catches bots: when it is filled in, the answer is "Thanks" and nothing is kept.
- At most 5 tries a minute per visitor (burst 3), and a 4 KB body. Behind Cloudflare the visitor is
  `CF-Connecting-IP`, but only when Traefik saw the request come from a Cloudflare address (the last
  `X-Forwarded-For` entry); otherwise the address Traefik saw. So nobody can pick their own rate-limit key by going
  around Cloudflare. This relies on the container being reachable only through Traefik (no published port). If
  Cloudflare's ranges change, update the `geo` block in `nginx.conf` from https://www.cloudflare.com/ips/.
- It works without JavaScript (a plain post, answered with a redirect to `/#notify-<result>`, and the page shows that
  message with CSS `:target`); `site.js` sends it with `fetch` instead and shows the answer under the form.

Tests: `node --test "site/test/*.test.mjs"` runs `njs/notify.js` under Node (validation, honeypot, repeats, the line
format). The Docker build also runs `nginx -t`, which compiles the njs file.

### The list is personal data

`hosted.jsonl` holds people's email addresses. It lives only on the server's volume: never commit it, an export of
it or any address from it to this repository, and don't paste it into issues or chats. (`.gitignore` ignores
`hosted*.jsonl` and `hosted-emails*.txt` as a safety net.) The privacy policy (`privacy.html`, "Hosted version
sign-up") promises it is used only to email people once if a hosted version launches, and deleted on request: email
to privacy@ficdev.xyz.

### Set up the volume in Dokploy (once)

Without it the list is inside the container and lost on the next deploy (the container log then warns
`/data/signups is not a mounted volume`).

1. Open the **homepage** application, tab **Advanced**, section **Volumes**, **Add Volume**.
2. Type **Volume Mount**. Volume name `homepage-signups`, mount path `/data/signups`. **Create**.
3. **Deploy** the application (mounts take effect on the next deploy).
4. Check: the form on the homepage says "Thanks, we'll let you know." for a test address, and the file has its line
   (below). Delete the test address again.

The container hands the folder to the `nginx` user at every start (`signups-dir.sh`), so the volume needs no
permissions set by hand. Dokploy's volume backups can include `homepage-signups` if you want a copy.

### Read, export and delete

On the Dokploy server, find the container first (the app name is on the application's **General** tab, for example
`homepage-abc123`):

```sh
c=$(docker ps -q -f name=homepage-abc123)
```

Read the list, count it, or export just the addresses (keep the export out of the repository):

```sh
docker exec $c cat /data/signups/hosted.jsonl
docker exec $c wc -l /data/signups/hosted.jsonl
docker exec $c cat /data/signups/hosted.jsonl | sed -n 's/^{"email":"\([^"]*\)".*/\1/p' > hosted-emails.txt
```

Delete one address (lowercase, as stored), run as the `nginx` user so the file keeps its owner:

```sh
docker exec -u nginx $c sh -c 'grep -vF "\"email\":\"$1\"" /data/signups/hosted.jsonl > /data/signups/.tmp; mv /data/signups/.tmp /data/signups/hosted.jsonl' - someone@example.com
```

After the one email has gone out, delete the whole list, as the privacy policy says:

```sh
docker exec $c rm /data/signups/hosted.jsonl
```

Dokploy's terminal for the container (Docker, the container, **Terminal**) takes the same commands without
`docker exec $c`. It runs as root, so after deleting an address there run `chown nginx:nginx
/data/signups/hosted.jsonl`, or new sign-ups fail until the container restarts.
