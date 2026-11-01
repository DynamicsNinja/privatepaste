// Browser port of PrivatePaste's end-to-end crypto (src/PrivatePaste.Protocol/Crypto). Formats and constants are
// pinned byte for byte by docs/e2e-test-vectors.json (tests/web/e2e.test.mjs). Never log keys, passphrases,
// one-time keys, decrypted payloads or handover QR text.

const enc = new TextEncoder();
const dec = new TextDecoder('utf-8', { fatal: true });
const subtle = globalThis.crypto.subtle;

const SMALL_VERSION = 0x01;
const BLOB_VERSION = 0x02;
const NONCE_SIZE = 12;
const TAG_SIZE = 16;
const CHUNK_SIZE = 1048576;
const PREFIX_SIZE = 8;
const OTK_SIZE = 32;
const VK_SIZE = 32;
const BLOB_AAD_LABEL = enc.encode('privatepaste v1 blob');
const HANDOVER_AAD = enc.encode('privatepaste v1 handover');
const QR_PREFIX = 'privatepaste:handover?';

// Cache-busting versions for the two code-built URLs (Cloudflare caches static files); see wwwroot/lib/README.md.
const HASHWASM_VERSION = '4.12.0';
const LIST_VERSION = '8ce67ae3';

const ARGON2 = { memoryKiB: 65536, iterations: 3, parallelism: 1, hashLength: 32, saltSize: 16 };
// Bounds for a wrap read from the server, the same on every client and the server: nothing weaker than what the
// clients write, and nothing so heavy that a hostile record could make the browser hang or run out of memory.
const ARGON2_MIN = { memoryKiB: 65536, iterations: 3, parallelism: 1 };
const ARGON2_MAX = { memoryKiB: 262144, iterations: 10, parallelism: 8 };

export const MIN_PASSPHRASE_LENGTH = 12;

export class WrongPassphraseError extends Error {
  constructor() {
    super('Wrong passphrase.');
    this.name = 'WrongPassphraseError';
  }
}

/** The wrap's settings or layout are outside what this client accepts (not a wrong passphrase). */
export class UnsupportedWrapError extends Error {
  constructor() {
    super('Unsupported passphrase wrap.');
    this.name = 'UnsupportedWrapError';
  }
}

// ---- byte helpers ----

const concat = (...parts) => {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
};

const randomBytes = n => globalThis.crypto.getRandomValues(new Uint8Array(n));

const toHex = bytes => Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');

const toBase64 = bytes => {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
};

const fromBase64 = text => Uint8Array.from(atob(text), c => c.charCodeAt(0));

const toBase64Url = bytes => toBase64(bytes).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

const fromBase64Url = text => fromBase64(text.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (text.length % 4)) % 4));

const u32be = n => new Uint8Array([(n >>> 24) & 0xff, (n >>> 16) & 0xff, (n >>> 8) & 0xff, n & 0xff]);

const bytesOf = (value, name) => {
  if (!(value instanceof Uint8Array)) throw new TypeError(`${name} must be a Uint8Array.`);
  return value;
};

// ---- vault key ----

async function hkdf(vk, info, bytes) {
  const base = await subtle.importKey('raw', vk, 'HKDF', false, ['deriveBits']);
  return new Uint8Array(await subtle.deriveBits(
    { name: 'HKDF', hash: 'SHA-256', salt: new Uint8Array(0), info: enc.encode(info) }, base, bytes * 8));
}

// Raw subkeys, exposed only so the tests can compare them with the shared vectors.
export async function _deriveSubkeys(vk) {
  return {
    encKey: await hkdf(vk, 'privatepaste v1 enc', 32),
    hashKey: await hkdf(vk, 'privatepaste v1 hash', 32),
    keyId: toHex((await hkdf(vk, 'privatepaste v1 key id', 32)).subarray(0, 8)),
  };
}

/** @returns {Promise<{keyId: string, encKey: CryptoKey, hashKey: CryptoKey, raw: Uint8Array}>} */
export async function importVaultKey(bytes) {
  bytesOf(bytes, 'The vault key');
  if (bytes.length !== VK_SIZE) throw new RangeError(`The vault key is ${VK_SIZE} bytes.`);
  const raw = Uint8Array.from(bytes);
  const sub = await _deriveSubkeys(raw);
  try {
    return {
      keyId: sub.keyId,
      encKey: await subtle.importKey('raw', sub.encKey, 'AES-GCM', false, ['encrypt', 'decrypt']),
      hashKey: await subtle.importKey('raw', sub.hashKey, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']),
      raw,
    };
  } finally {
    sub.encKey.fill(0);
    sub.hashKey.fill(0);
  }
}

// ---- small items: 0x01 || nonce(12) || ciphertext || tag(16) ----

const purposeAad = purpose => {
  if (purpose !== 'payload' && purpose !== 'thumb') throw new RangeError(`Unknown purpose '${purpose}'.`);
  return enc.encode('privatepaste v1 ' + purpose);
};

async function sealWith(aesKey, aad, bytes, nonce) {
  if (nonce.length !== NONCE_SIZE) throw new RangeError(`The nonce is ${NONCE_SIZE} bytes.`);
  const body = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad, tagLength: 128 }, aesKey, bytes));
  return concat(new Uint8Array([SMALL_VERSION]), nonce, body);
}

async function openWith(aesKey, aad, sealed) {
  if (sealed.length < 1 + NONCE_SIZE + TAG_SIZE || sealed[0] !== SMALL_VERSION) throw new Error('Not a sealed item.');
  return new Uint8Array(await subtle.decrypt(
    { name: 'AES-GCM', iv: sealed.subarray(1, 1 + NONCE_SIZE), additionalData: aad, tagLength: 128 },
    aesKey, sealed.subarray(1 + NONCE_SIZE)));
}

export const sealSmall = async (vault, purpose, bytes) =>
  sealWith(vault.encKey, purposeAad(purpose), bytesOf(bytes, 'The plaintext'), randomBytes(NONCE_SIZE));

export const openSmall = async (vault, purpose, bytes) => openWith(vault.encKey, purposeAad(purpose), bytesOf(bytes, 'The sealed item'));

// Fixed nonce, for the test vectors only: a nonce must never repeat under one key.
export const _sealWithNonce = async (vault, purpose, bytes, nonce) => sealWith(vault.encKey, purposeAad(purpose), bytes, nonce);

// ---- blobs: 0x02 || noncePrefix(8), then 1 MiB chunks of ciphertext || tag ----

const blobNonce = (prefix, index) => concat(prefix, u32be(index));
const blobAad = (index, isFinal) => concat(BLOB_AAD_LABEL, u32be(index), new Uint8Array([isFinal ? 1 : 0]));

// Fixed prefix, for the test vectors only.
export async function _encryptBlobWithPrefix(vault, bytes, prefix) {
  bytesOf(bytes, 'The plaintext');
  if (prefix.length !== PREFIX_SIZE) throw new RangeError(`The nonce prefix is ${PREFIX_SIZE} bytes.`);
  const parts = [concat(new Uint8Array([BLOB_VERSION]), prefix)];
  // An empty input is one final chunk with an empty plaintext.
  const count = Math.max(1, Math.ceil(bytes.length / CHUNK_SIZE));
  for (let index = 0; index < count; index++) {
    const chunk = bytes.subarray(index * CHUNK_SIZE, (index + 1) * CHUNK_SIZE);
    parts.push(new Uint8Array(await subtle.encrypt(
      { name: 'AES-GCM', iv: blobNonce(prefix, index), additionalData: blobAad(index, index === count - 1), tagLength: 128 },
      vault.encKey, chunk)));
  }
  return concat(...parts);
}

export const encryptBlob = (vault, bytes) => _encryptBlobWithPrefix(vault, bytes, randomBytes(PREFIX_SIZE));

export async function decryptBlob(vault, bytes) {
  bytesOf(bytes, 'The blob');
  if (bytes.length < 1 + PREFIX_SIZE || bytes[0] !== BLOB_VERSION) throw new Error('Not an encrypted blob.');
  const prefix = bytes.subarray(1, 1 + PREFIX_SIZE);
  const body = bytes.subarray(1 + PREFIX_SIZE);
  const sealedChunk = CHUNK_SIZE + TAG_SIZE;
  const count = Math.ceil(body.length / sealedChunk);
  // The last chunk is the one that ends the input, so a dropped, reordered or appended chunk fails to authenticate.
  if (count === 0 || body.length - (count - 1) * sealedChunk < TAG_SIZE) throw new Error('The blob is truncated.');
  const parts = [];
  for (let index = 0; index < count; index++) {
    parts.push(new Uint8Array(await subtle.decrypt(
      { name: 'AES-GCM', iv: blobNonce(prefix, index), additionalData: blobAad(index, index === count - 1), tagLength: 128 },
      vault.encKey, body.subarray(index * sealedChunk, (index + 1) * sealedChunk))));
  }
  return concat(...parts);
}

// ---- hashes ----

const sha256Hex = async bytes => toHex(new Uint8Array(await subtle.digest('SHA-256', bytes)));

/** The server-visible hash: lowercase hex of HMAC-SHA256(hashKey, UTF-8(ContentHash hex)). */
export async function keyedHash(vault, contentHashHex) {
  return toHex(new Uint8Array(await subtle.sign('HMAC', vault.hashKey, enc.encode(contentHashHex))));
}

/** ContentHash.ForText: SHA256(UTF8("t:" + text with CRLF and lone CR turned into LF)). */
export const contentHashText = text => sha256Hex(enc.encode('t:' + text.replace(/\r\n/g, '\n').replace(/\r/g, '\n')));

/** ContentHash.ForPixels: SHA256(UTF8("i:{w}x{h}:") ++ rgba), rgba being unpremultiplied RGBA8888 with no row padding. */
export const contentHashPixels = (rgba, width, height) => sha256Hex(concat(enc.encode(`i:${width}x${height}:`), rgba));

// ---- payload: base64 of a small item holding UTF-8 JSON ----

export async function sealPayload(vault, obj) {
  return toBase64(await sealSmall(vault, 'payload', enc.encode(JSON.stringify(obj))));
}

export async function openPayload(vault, base64) {
  return JSON.parse(dec.decode(await openSmall(vault, 'payload', fromBase64(base64))));
}

// ---- passphrase wrap ----

let hashwasmPromise;
function loadHashwasm() {
  hashwasmPromise ??= (async () => {
    if (globalThis.hashwasm?.argon2id) return globalThis.hashwasm;
    const url = new URL('../lib/hash-wasm/argon2.umd.min.js', import.meta.url);
    url.search = `v=${HASHWASM_VERSION}`;
    if (typeof document === 'undefined') {
      // Node (the tests): the UMD file loads as CommonJS.
      const mod = await import(url.href);
      return mod.default?.argon2id ? mod.default : mod;
    }
    await new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = url.href;
      script.onload = resolve;
      script.onerror = () => reject(new Error('Could not load the passphrase library.'));
      document.head.append(script);
    });
    return globalThis.hashwasm;
  })();
  hashwasmPromise.catch(() => { hashwasmPromise = undefined; });
  return hashwasmPromise;
}

async function argon2Raw(passphrase, salt, kdf) {
  const { argon2id } = await loadHashwasm();
  return argon2id({
    password: enc.encode(passphrase.normalize('NFC')),
    salt,
    parallelism: kdf.parallelism,
    iterations: kdf.iterations,
    memorySize: kdf.memoryKiB,
    hashLength: ARGON2.hashLength,
    outputType: 'binary',
  });
}

// The raw 32-byte passphrase key, exposed only so the tests can compare it with the shared vectors.
export const _argon2Key = (passphrase, salt) => argon2Raw(passphrase, salt, ARGON2);

async function passphraseKey(passphrase, salt, kdf) {
  const raw = await argon2Raw(passphrase, salt, kdf);
  try {
    return await subtle.importKey('raw', raw, 'AES-GCM', false, ['encrypt', 'decrypt']);
  } finally {
    raw.fill(0);
  }
}

const vaultAad = keyId => enc.encode('privatepaste v1 vault ' + keyId);

// Salt and nonce are parameters so the tests can pin them; the public function draws them randomly.
export async function _wrapWithPassphrase(vault, passphrase, salt, nonce) {
  const key = await passphraseKey(passphrase, salt, ARGON2);
  const wrapped = new Uint8Array(await subtle.encrypt(
    { name: 'AES-GCM', iv: nonce, additionalData: vaultAad(vault.keyId), tagLength: 128 }, key, vault.raw));
  return {
    keyId: vault.keyId,
    kdf: { alg: 'argon2id', memoryKiB: ARGON2.memoryKiB, iterations: ARGON2.iterations, parallelism: ARGON2.parallelism, salt: toBase64(salt) },
    nonce: toBase64(nonce),
    wrappedKey: toBase64(wrapped),
  };
}

export const wrapWithPassphrase = (vault, passphrase) =>
  _wrapWithPassphrase(vault, passphrase, randomBytes(ARGON2.saltSize), randomBytes(NONCE_SIZE));

/**
 * @throws {WrongPassphraseError} The passphrase doesn't open the wrap.
 * @throws {UnsupportedWrapError} The wrap's settings are outside the bounds every client accepts.
 */
export async function unwrapWithPassphrase(wrapDto, passphrase) {
  const kdf = wrapDto?.kdf;
  const within = name => Number.isInteger(kdf[name]) && kdf[name] >= ARGON2_MIN[name] && kdf[name] <= ARGON2_MAX[name];
  if (kdf?.alg !== 'argon2id' || !within('memoryKiB') || !within('iterations') || !within('parallelism')
    || typeof wrapDto.keyId !== 'string')
    throw new UnsupportedWrapError();
  // Lengths .NET AesGcm demands, so the browser rejects what .NET rejects.
  let salt, nonce, wrapped;
  try {
    salt = fromBase64(kdf.salt);
    nonce = fromBase64(wrapDto.nonce);
    wrapped = fromBase64(wrapDto.wrappedKey);
  } catch {
    throw new UnsupportedWrapError();
  }
  if (salt.length !== ARGON2.saltSize || nonce.length !== NONCE_SIZE) throw new UnsupportedWrapError();
  const key = await passphraseKey(passphrase, salt, kdf);
  let vk;
  try {
    vk = new Uint8Array(await subtle.decrypt(
      { name: 'AES-GCM', iv: nonce, additionalData: vaultAad(wrapDto.keyId), tagLength: 128 }, key, wrapped));
  } catch {
    throw new WrongPassphraseError();
  }
  let vault;
  try { vault = await importVaultKey(vk); } finally { vk.fill(0); }
  if (vault.keyId !== wrapDto.keyId) throw new Error('The wrap holds a different vault key.');
  return vault;
}

// ---- handover ----

// Fixed nonce, for the test vectors only.
export async function _createHandoverWithNonce(vault, otk, nonce) {
  const key = await subtle.importKey('raw', otk, 'AES-GCM', false, ['encrypt']);
  return sealWith(key, HANDOVER_AAD, vault.raw, nonce);
}

export async function createHandover(vault) {
  const otk = randomBytes(OTK_SIZE);
  return { otk, blob: await _createHandoverWithNonce(vault, otk, randomBytes(NONCE_SIZE)) };
}

/** @throws The one-time key is wrong, the blob was tampered with, or it holds another vault key than `keyId`. */
export async function openHandover(otk, blob, keyId) {
  bytesOf(otk, 'The handover key');
  if (otk.length !== OTK_SIZE) throw new Error('The handover key is the wrong length.');
  const key = await subtle.importKey('raw', otk, 'AES-GCM', false, ['decrypt']);
  const vk = await openWith(key, HANDOVER_AAD, bytesOf(blob, 'The handover'));
  if (vk.length !== VK_SIZE) throw new Error('The handover holds no vault key.');
  let vault;
  try { vault = await importVaultKey(vk); } finally { vk.fill(0); }
  if (vault.keyId !== keyId) throw new Error('The handover holds a different vault key.');
  return vault;
}

export const handoverQrText = (id, otk, keyId) => `${QR_PREFIX}id=${id}&k=${toBase64Url(otk)}&kid=${keyId}`;

/** @returns {{id: string, otk: Uint8Array, keyId: string} | null} null when the text isn't a valid handover code. */
export function parseHandoverQr(text) {
  if (typeof text !== 'string' || !text.startsWith(QR_PREFIX)) return null;
  const found = {};
  for (const pair of text.slice(QR_PREFIX.length).split('&')) {
    const eq = pair.indexOf('=');
    if (eq < 0) continue;
    const name = pair.slice(0, eq);
    // A repeated field is ambiguous, so the whole code is rejected; unknown fields are left for later versions.
    if (name === 'id' || name === 'k' || name === 'kid') {
      if (name in found) return null;
      found[name] = pair.slice(eq + 1);
    }
  }
  const { id, k, kid } = found;
  if (!/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(id ?? '')
    || !/^[0-9a-f]{16}$/.test(kid ?? '')
    || !/^[A-Za-z0-9_-]{43}$/.test(k ?? ''))
    return null;
  const otk = fromBase64Url(k);
  // Canonical spelling only (no stray trailing bits), the same as the .NET and Kotlin parsers.
  return otk.length === OTK_SIZE && toBase64Url(otk) === k ? { id: id.toLowerCase(), otk, keyId: kid } : null;
}

// ---- passphrase rule ----

export const PASSPHRASE_TOO_SHORT = 'Use at least 12 characters. A short sentence works well.';
export const PASSPHRASE_TOO_COMMON = 'That passphrase is too common. Pick another.';

let commonPromise;
function loadCommon() {
  commonPromise ??= (async () => {
    const url = new URL('./common-passphrases.txt', import.meta.url);
    url.search = `v=${LIST_VERSION}`;
    let text;
    if (typeof document === 'undefined' && url.protocol === 'file:') {
      text = await (await import('node:fs/promises')).readFile(url, 'utf8');
    } else {
      const response = await fetch(url);
      if (!response.ok) throw new Error('Could not load the common-passphrase list.');
      text = await response.text();
    }
    return new Set(text.split(/\r?\n/).filter(line => line && !line.startsWith('#')));
  })();
  commonPromise.catch(() => { commonPromise = undefined; });
  return commonPromise;
}

/** @returns {Promise<string | null>} What to tell the user, or null when the passphrase is acceptable. */
export async function passphraseProblem(passphrase) {
  const normalized = passphrase.normalize('NFC');
  if (normalized.length < MIN_PASSPHRASE_LENGTH) return PASSPHRASE_TOO_SHORT;
  return (await loadCommon()).has(normalized.toLowerCase()) ? PASSPHRASE_TOO_COMMON : null;
}
