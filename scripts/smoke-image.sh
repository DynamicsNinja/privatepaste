#!/usr/bin/env bash
# Smoke-tests a built server image: starts it, signs in, sets up a vault, posts an end-to-end encrypted text clip
# and image clip, restarts the container and checks the encrypted database reopens with the clips. The server never
# decrypts anything, so the vault wrap and the clip payloads only need to be well-formed, not real ciphertext.
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

b64() { head -c "$1" /dev/urandom | base64 -w0; }

docker run -d --name "$name" --platform "$platform" -p "$port:8080" \
  -e PP_DB_KEY=smoke-key-0123456789abcdef -e PP_PASSWORD=smoke "$image" >/dev/null
wait_healthy

curl -fs "$base/api/server-info" | jq -e '.protocol == 2' >/dev/null || fail "server info"

token=$(curl -fs -X POST "$base/api/auth/login" -H 'content-type: application/json' \
  -d '{"username":"owner","password":"smoke","deviceName":"Smoke","platform":"linux"}' | jq -r .token) || fail "sign-in"
auth=(-H "authorization: Bearer $token" -H "X-PrivatePaste-Protocol: 2")

# An app without the protocol header is told to update.
status=$(curl -s -o /dev/null -w '%{http_code}' "$base/api/clips" -H "authorization: Bearer $token")
[ "$status" = 426 ] || fail "expected 426 without the protocol header, got $status"

kid=0123456789abcdef
curl -fs -X PUT "$base/api/vault" "${auth[@]}" -H 'content-type: application/json' \
  -d "{\"keyId\":\"$kid\",\"kdf\":{\"alg\":\"argon2id\",\"memoryKiB\":65536,\"iterations\":3,\"parallelism\":1,\"salt\":\"$(b64 16)\"},\"nonce\":\"$(b64 12)\",\"wrappedKey\":\"$(b64 48)\"}" \
  >/dev/null || fail "vault setup"

text_payload=$(b64 45)
curl -fs -X POST "$base/api/clips" "${auth[@]}" \
  -F "meta={\"kind\":\"text\",\"keyId\":\"$kid\",\"hash\":\"$(printf 'a%.0s' $(seq 64))\",\"payload\":\"$text_payload\",\"sizeBytes\":5,\"backfill\":false};type=application/json" \
  >/dev/null || fail "text upload"

blob=$(mktemp); thumb=$(mktemp)
head -c 4096 /dev/urandom > "$blob"; head -c 512 /dev/urandom > "$thumb"
out=$(curl -sS --fail-with-body -X POST "$base/api/clips" "${auth[@]}" \
  -F "meta={\"kind\":\"image\",\"keyId\":\"$kid\",\"hash\":\"$(printf 'b%.0s' $(seq 64))\",\"payload\":\"$(b64 60)\",\"sizeBytes\":4000,\"backfill\":false};type=application/json" \
  -F "thumb=@$thumb;type=application/octet-stream" -F "blob0=@$blob;type=application/octet-stream") \
  || fail "image upload: $out"

docker restart "$name" >/dev/null
wait_healthy
curl -fs "$base/api/clips" "${auth[@]}" \
  | jq -e --arg p "$text_payload" '[.items[].kind] == ["image","text"] and .items[1].payload == $p
      and .items[0].hasThumb == true and .items[0].blobCount == 1 and .items[0].sourceDevicePlatform == "linux"' >/dev/null \
  || fail "clips missing after restart"
id=$(curl -fs "$base/api/clips" "${auth[@]}" | jq -r '.items[0].id')
curl -fs "$base/api/clips/$id/blobs/0" "${auth[@]}" | cmp -s - "$blob" || fail "image blob changed after restart"
rm -f "$blob" "$thumb"

echo "OK ($platform): health, protocol gate, sign-in, vault, encrypted text and image upload, restart with data intact"
