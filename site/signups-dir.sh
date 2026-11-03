#!/bin/sh
# Run by the nginx image's entrypoint (as root) before nginx starts. The sign-up list (/data/signups/hosted.jsonl,
# see site/README.md) must be writable by the nginx workers and readable by no one else.
set -e
if ! grep -q ' /data/signups ' /proc/mounts; then
    echo "$0: WARNING /data/signups is not a mounted volume: sign-ups are lost on the next deploy (see site/README.md)" >&2
fi
mkdir -p /data/signups
chown nginx:nginx /data/signups
chmod 700 /data/signups
if [ -f /data/signups/hosted.jsonl ]; then
    chown nginx:nginx /data/signups/hosted.jsonl
    chmod 600 /data/signups/hosted.jsonl
fi
