#!/usr/bin/env bash
# Smoke-tests a built server image: starts it, signs in, posts a text and an image clip (the image exercises
# SkiaSharp's native library), restarts the container and checks the encrypted database reopens with the clips.
#
#   scripts/smoke-image.sh <image> [platform]      e.g. scripts/smoke-image.sh privatepaste:smoke linux/arm64
set -euo pipefail
image=$1
platform=${2:-linux/amd64}
name=pp-smoke-${platform//\//-}
port=${PP_SMOKE_PORT:-18080}
base=http://localhost:$port

cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT
fail() { echo "FAIL ($platform): $*" >&2; docker logs "$name" >&2 || true; exit 1; }

wait_healthy() {
  for _ in $(seq 120); do
    curl -fs "$base/health" >/dev/null 2>&1 && return 0
    sleep 1
  done
  fail "no answer on /health"
}

docker run -d --name "$name" --platform "$platform" -p "$port:8080" \
  -e PP_DB_KEY=smoke-key-0123456789abcdef -e PP_PASSWORD=smoke "$image" >/dev/null
wait_healthy

token=$(curl -fs -X POST "$base/api/auth/login" -H 'content-type: application/json' \
  -d '{"username":"owner","password":"smoke","deviceName":"Smoke","platform":"linux"}' | jq -r .token) || fail "sign-in"
auth=(-H "authorization: Bearer $token")

curl -fs -X POST "$base/api/clips/text" "${auth[@]}" -H 'content-type: application/json' \
  -d '{"text":"hello from the smoke test","backfill":false}' >/dev/null || fail "text upload"

# 1x1 red PNG
png=$(mktemp --suffix=.png)
echo 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==' | base64 -d > "$png"
curl -fs -X POST "$base/api/clips/image" "${auth[@]}" -F "file=@$png;type=image/png" >/dev/null || fail "image upload"
rm -f "$png"

docker restart "$name" >/dev/null
wait_healthy
curl -fs "$base/api/clips" "${auth[@]}" \
  | jq -e '[.items[].kind] == ["image","text"] and .items[1].text == "hello from the smoke test" and .items[0].sourceDevicePlatform == "linux"' >/dev/null \
  || fail "clips missing after restart"

echo "OK ($platform): health, sign-in, text and image upload, restart with data intact"
