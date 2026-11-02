import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import * as e2e from '../../src/PrivatePaste.Server/wwwroot/js/e2e.js';

const vectors = JSON.parse(readFileSync(new URL('../../docs/e2e-test-vectors.json', import.meta.url), 'utf8'));
const hex = h => Uint8Array.from(Buffer.from(h, 'hex'));
const toHex = b => Buffer.from(b).toString('hex');
const sha256 = b => createHash('sha256').update(b).digest('hex');
const patterned = n => Uint8Array.from({ length: n }, (_, i) => i % 251);

const vault = await e2e.importVaultKey(hex(vectors.vaultKey.vk));

test('subkeys and key id match the vectors', async () => {
  const sub = await e2e._deriveSubkeys(hex(vectors.vaultKey.vk));
  assert.equal(toHex(sub.encKey), vectors.vaultKey.encKey);
  assert.equal(toHex(sub.hashKey), vectors.vaultKey.hashKey);
  assert.equal(sub.keyId, vectors.vaultKey.keyId);
  assert.equal(vault.keyId, vectors.vaultKey.keyId);
  assert.equal(toHex(vault.raw), vectors.vaultKey.vk);
});

test('importVaultKey rejects a key of the wrong length', async () => {
  await assert.rejects(() => e2e.importVaultKey(new Uint8Array(31)), RangeError);
});

test('small item with the fixed nonce matches the vector, and opens', async () => {
  const p = vectors.payload;
  const sealed = await e2e._sealWithNonce(vault, p.purpose, new TextEncoder().encode(p.json), hex(p.nonce));
  assert.equal(toHex(sealed), p.sealed);
  assert.equal(new TextDecoder().decode(await e2e.openSmall(vault, p.purpose, hex(p.sealed))), p.json);
});

test('small item is bound to its purpose and to tampering', async () => {
  const sealed = await e2e.sealSmall(vault, 'thumb', new Uint8Array([1, 2, 3]));
  assert.deepEqual([...await e2e.openSmall(vault, 'thumb', sealed)], [1, 2, 3]);
  await assert.rejects(() => e2e.openSmall(vault, 'payload', sealed));
  const bad = Uint8Array.from(sealed);
  bad[bad.length - 1] ^= 1;
  await assert.rejects(() => e2e.openSmall(vault, 'thumb', bad));
  await assert.rejects(() => e2e.sealSmall(vault, 'blob', sealed), RangeError);
});

test('payload opens from a base64 vector and round-trips', async () => {
  const b64 = Buffer.from(hex(vectors.payload.sealed)).toString('base64');
  assert.deepEqual(await e2e.openPayload(vault, b64), { text: 'hello' });
  const again = await e2e.sealPayload(vault, { text: 'héllo', width: 2, height: 3, future: true });
  assert.deepEqual(await e2e.openPayload(vault, again), { text: 'héllo', width: 2, height: 3, future: true });
});

test('blob matches the vector digests and round-trips', async () => {
  const b = vectors.blob;
  const plain = patterned(b.plaintextLength);
  assert.equal(sha256(plain), b.plaintextSha256);
  const sealed = await e2e._encryptBlobWithPrefix(vault, plain, hex(b.noncePrefix));
  assert.equal(sealed.length, b.ciphertextLength);
  assert.equal(sha256(sealed), b.ciphertextSha256);
  assert.equal(sha256(await e2e.decryptBlob(vault, sealed)), b.plaintextSha256);
});

test('empty blob matches the vector', async () => {
  const sealed = await e2e._encryptBlobWithPrefix(vault, new Uint8Array(0), hex(vectors.emptyBlob.noncePrefix));
  assert.equal(toHex(sealed), vectors.emptyBlob.ciphertext);
  assert.equal((await e2e.decryptBlob(vault, sealed)).length, 0);
});

test('blob handles an exact chunk multiple and rejects truncation, reordering and trailing data', async () => {
  const two = patterned(2 * 1048576);
  const sealed = await e2e.encryptBlob(vault, two);
  assert.equal(sealed.length, 9 + 2 * (1048576 + 16));
  assert.equal(sha256(await e2e.decryptBlob(vault, sealed)), sha256(two));

  const chunk = 1048576 + 16;
  await assert.rejects(() => e2e.decryptBlob(vault, sealed.subarray(0, 9 + chunk)), 'dropped last chunk');
  await assert.rejects(() => e2e.decryptBlob(vault, sealed.subarray(0, sealed.length - 1)), 'cut tag');
  const swapped = Uint8Array.from(sealed);
  swapped.set(sealed.subarray(9 + chunk), 9);
  swapped.set(sealed.subarray(9, 9 + chunk), 9 + chunk);
  await assert.rejects(() => e2e.decryptBlob(vault, swapped), 'reordered');
  const extra = new Uint8Array(sealed.length + 32);
  extra.set(sealed);
  await assert.rejects(() => e2e.decryptBlob(vault, extra), 'trailing data');
  await assert.rejects(() => e2e.decryptBlob(vault, new Uint8Array([2, 1, 2])), 'short header');
});

test('keyed hash and content hashes match the vector', async () => {
  const k = vectors.keyedHash;
  assert.equal(await e2e.contentHashText(k.text), k.contentHash);
  assert.equal(await e2e.keyedHash(vault, k.contentHash), k.keyedHash);
  assert.equal(await e2e.contentHashText('a\r\nb\rc'), await e2e.contentHashText('a\nb\nc'));
});

test('pixel hash is SHA-256 of the "i:WxH:" label and the pixels', async () => {
  const rgba = Uint8Array.from([1, 2, 3, 4, 5, 6, 7, 8]);
  const expected = sha256(Buffer.concat([Buffer.from('i:2x1:'), rgba]));
  assert.equal(await e2e.contentHashPixels(rgba, 2, 1), expected);
});

test('handover matches the vector, opens, and the QR text round-trips', async () => {
  const h = vectors.handover;
  const blob = await e2e._createHandoverWithNonce(vault, hex(h.otk), hex(h.nonce));
  assert.equal(toHex(blob), h.blob);
  const opened = await e2e.openHandover(hex(h.otk), hex(h.blob), vectors.vaultKey.keyId);
  assert.equal(toHex(opened.raw), vectors.vaultKey.vk);
  await assert.rejects(() => e2e.openHandover(hex(h.otk), hex(h.blob), '0000000000000000'));
  await assert.rejects(() => e2e.openHandover(new Uint8Array(32), hex(h.blob), vectors.vaultKey.keyId));

  assert.equal(e2e.handoverQrText(h.id, hex(h.otk), vectors.vaultKey.keyId), h.qrText);
  const parsed = e2e.parseHandoverQr(h.qrText);
  assert.equal(parsed.id, h.id);
  assert.equal(toHex(parsed.otk), h.otk);
  assert.equal(parsed.keyId, vectors.vaultKey.keyId);

  const fresh = await e2e.createHandover(vault);
  assert.equal(toHex((await e2e.openHandover(fresh.otk, fresh.blob, vault.keyId)).raw), toHex(vault.raw));
});

test('parseHandoverQr rejects malformed codes', () => {
  const ok = vectors.handover.qrText;
  assert.ok(e2e.parseHandoverQr(ok + '&future=1'));
  for (const bad of [
    'https://example.com', ok + '&id=00000000-0000-4000-8000-000000000002', ok.replace('&kid=', '&kid=ZZ'),
    ok.replace(/&k=[^&]*/, '&k=AAAA'), ok.replace(/&kid=.*/, ''), ok.replace('id=0000', 'id=xxxx'),
  ]) assert.equal(e2e.parseHandoverQr(bad), null, bad);
});

// The passphrase task fills vectors.argon2 with { passphrase, salt, key } cases (hex salt and key).
test('argon2 vectors reproduce (when present)', async () => {
  for (const v of vectors.argon2) {
    const salt = hex(v.salt);
    assert.equal(toHex(await e2e._argon2Key(v.passphrase, salt)), v.key, v.passphrase);
    assert.equal(toHex(await e2e._argon2Key(v.passphrase.normalize('NFD'), salt)), v.key, 'NFD');
    assert.equal(toHex(await e2e._argon2Key(v.passphrase.normalize('NFC'), salt)), v.key, 'NFC');
  }
});

// The passphrase task fills vectors.passphraseWrap with { passphrase, wrap: <VaultWrap DTO>, vk: hex }.
test('passphraseWrap vector unwraps to the vault key (when present)', async () => {
  const cases = vectors.passphraseWrap ?? [];
  for (const v of Array.isArray(cases) ? cases : [cases]) {
    assert.equal(toHex((await e2e.unwrapWithPassphrase(v.wrap, v.passphrase)).raw), v.vk);
    assert.equal(toHex((await e2e.unwrapWithPassphrase(v.wrap, v.passphrase.normalize('NFD'))).raw), v.vk);
  }
});

test('passphrase wrap round-trips, rejects a wrong passphrase, and treats NFD and NFC alike', async () => {
  const nfc = 'café au lait, süss';
  const nfd = nfc.normalize('NFD');
  assert.notEqual(nfc, nfd);
  const wrap = await e2e.wrapWithPassphrase(vault, nfc);
  assert.equal(wrap.keyId, vault.keyId);
  assert.deepEqual({ ...wrap.kdf, salt: undefined }, { alg: 'argon2id', memoryKiB: 65536, iterations: 3, parallelism: 1, salt: undefined });
  assert.equal(Buffer.from(wrap.kdf.salt, 'base64').length, 16);
  assert.equal(Buffer.from(wrap.nonce, 'base64').length, 12);
  assert.equal(Buffer.from(wrap.wrappedKey, 'base64').length, 32 + 16);

  assert.equal(toHex((await e2e.unwrapWithPassphrase(wrap, nfc)).raw), toHex(vault.raw));
  assert.equal(toHex((await e2e.unwrapWithPassphrase(wrap, nfd)).raw), toHex(vault.raw));
  await assert.rejects(() => e2e.unwrapWithPassphrase(wrap, 'not the passphrase'), e2e.WrongPassphraseError);
  await assert.rejects(() => e2e.unwrapWithPassphrase({ ...wrap, keyId: '0000000000000000' }, nfc), e2e.WrongPassphraseError);
  await assert.rejects(() => e2e.unwrapWithPassphrase({ ...wrap, kdf: { ...wrap.kdf, memoryKiB: 1 << 30 } }, nfc), /Unsupported/);
  // The project-wide bounds: weaker settings than the clients write are refused as well as heavier ones.
  assert.equal(typeof e2e.UnsupportedWrapError, 'function');
  for (const kdf of [{ memoryKiB: 65535 }, { memoryKiB: 262145 }, { iterations: 2 }, { iterations: 11 }, { parallelism: 0 }, { parallelism: 9 }])
    await assert.rejects(() => e2e.unwrapWithPassphrase({ ...wrap, kdf: { ...wrap.kdf, ...kdf } }, nfc), e2e.UnsupportedWrapError);
  const b64 = n => Buffer.alloc(n, 1).toString('base64');
  await assert.rejects(() => e2e.unwrapWithPassphrase({ ...wrap, nonce: b64(11) }, nfc), /Unsupported/);
  await assert.rejects(() => e2e.unwrapWithPassphrase({ ...wrap, kdf: { ...wrap.kdf, salt: b64(15) } }, nfc), /Unsupported/);
});

test('passphraseProblem enforces length and the common list', async () => {
  assert.equal(await e2e.passphraseProblem('short one'), 'Use at least 12 characters. A short sentence works well.');
  assert.equal(await e2e.passphraseProblem('password1234'), 'That passphrase is too common. Pick another.');
  assert.equal(await e2e.passphraseProblem('PassWord1234'), 'That passphrase is too common. Pick another.');
  assert.equal(await e2e.passphraseProblem('purple walrus eats pancakes'), null);
});

test('the common-passphrase list only holds entries the length rule would not already reject', () => {
  const lines = readFileSync(new URL('../../src/PrivatePaste.Server/wwwroot/js/common-passphrases.txt', import.meta.url), 'utf8').split('\n');
  assert.ok(lines[0].startsWith('#') && lines[1].startsWith('#'));
  const body = lines.slice(2).filter(Boolean);
  assert.ok(body.length > 5000);
  assert.equal(new Set(body).size, body.length);
  assert.ok(body.every(l => l === l.toLowerCase() && l.length >= 12));
});

test('the list version in e2e.js is the start of the list SHA-256', () => {
  const dir = '../../src/PrivatePaste.Server/wwwroot/js/';
  const digest = sha256(readFileSync(new URL(dir + 'common-passphrases.txt', import.meta.url)));
  assert.match(readFileSync(new URL(dir + 'e2e.js', import.meta.url), 'utf8'), new RegExp(`LIST_VERSION = '${digest.slice(0, 8)}'`));
});

test('handover with - and _ in the key text matches the vector and round-trips', async () => {
  const h = vectors.handoverUrlSafe;
  assert.match(h.qrText, /&k=[^&]*-[^&]*&/);
  assert.match(h.qrText, /&k=[^&]*_[^&]*&/);
  assert.equal(toHex(await e2e._createHandoverWithNonce(vault, hex(h.otk), hex(h.nonce))), h.blob);
  assert.equal(toHex((await e2e.openHandover(hex(h.otk), hex(h.blob), vectors.vaultKey.keyId)).raw), vectors.vaultKey.vk);
  assert.equal(e2e.handoverQrText(h.id, hex(h.otk), vectors.vaultKey.keyId), h.qrText);
  const parsed = e2e.parseHandoverQr(h.qrText);
  assert.equal(parsed.id, h.id);
  assert.equal(toHex(parsed.otk), h.otk);
});

test('parseHandoverQr only accepts the canonical key spelling', () => {
  const ok = vectors.handover.qrText;
  // The last character of a 32-byte key carries two unused bits; the next character up sets one.
  assert.equal(e2e.parseHandoverQr(ok.replace('Pj8&', 'Pj9&')), null);
  assert.equal(e2e.parseHandoverQr(ok.replace('Pj8&', 'Pj8=&')), null);
});

test('thumb small item with the fixed nonce matches the vector, and opens', async () => {
  const t = vectors.thumb;
  assert.equal(toHex(await e2e._sealWithNonce(vault, t.purpose, hex(t.plaintext), hex(t.nonce))), t.sealed);
  assert.equal(toHex(await e2e.openSmall(vault, 'thumb', hex(t.sealed))), t.plaintext);
  await assert.rejects(() => e2e.openSmall(vault, 'payload', hex(t.sealed)));
});

test('contentHashFiles reproduces the fileHash vector', async () => {
  assert.equal(await e2e.contentHashFiles(vectors.fileHash.files), vectors.fileHash.expected);
});

test('contentHashFiles matches a hand-built string and depends on order, name and size', async () => {
  const a = { name: 'ä.txt', size: 3, sha256Hex: sha256(Buffer.from('abc')) };
  const b = { name: 'b', size: 0, sha256Hex: sha256(Buffer.alloc(0)) };
  const manual = sha256(Buffer.from(`f:ä.txt\u00003\u0000${a.sha256Hex}\nb\u00000\u0000${b.sha256Hex}\n`, 'utf8'));
  assert.equal(await e2e.contentHashFiles([a, b]), manual);
  assert.notEqual(await e2e.contentHashFiles([b, a]), manual);
  assert.notEqual(await e2e.contentHashFiles([{ ...a, size: 4 }, b]), manual);
});

test('safeFileName reproduces every vector', () => {
  for (const { in: input, out } of vectors.safeFileName)
    assert.equal(e2e.safeFileName(input), out, JSON.stringify(input));
});

test('formatSize reproduces every fileSize vector', () => {
  for (const { bytes, text } of vectors.fileSize) assert.equal(e2e.formatSize(bytes), text, String(bytes));
});

test('fileRowText reproduces every vector', () => {
  for (const { files, text } of vectors.fileRowText) assert.equal(e2e.fileRowText(files), text);
});

test('payloadFiles: one safe-named file per blob, or null like the .NET and Kotlin Decode', () => {
  const payload = { files: [{ name: '../a\nb.pdf', size: 3, mime: 'application/pdf' }, { name: 'c.txt', size: 1 }] };
  assert.deepEqual(e2e.payloadFiles(payload, 2), [{ name: 'ab.pdf', size: 3 }, { name: 'c.txt', size: 1 }]);
  assert.equal(e2e.payloadFiles(payload, 1), null);   // fewer blobs than files
  assert.equal(e2e.payloadFiles(payload, 3), null);   // more blobs than files
  assert.equal(e2e.payloadFiles(payload, undefined), null);
  assert.equal(e2e.payloadFiles({ files: [] }, 0), null);
  assert.equal(e2e.payloadFiles({ text: 'x' }, 1), null);
});

test('matchesFilter: All shows every kind, a type shows only its own, an unknown kind only under All', () => {
  for (const kind of ['text', 'link', 'image', 'file', 'hologram']) assert.equal(e2e.matchesFilter(kind, 'all'), true);
  for (const filter of ['text', 'link', 'image', 'file']) {
    for (const kind of ['text', 'link', 'image', 'file']) assert.equal(e2e.matchesFilter(kind, filter), kind === filter);
    assert.equal(e2e.matchesFilter('hologram', filter), false);
  }
});

test('nextFilter: tapping a type selects it, tapping it again or All returns to All', () => {
  assert.equal(e2e.nextFilter('all', 'link'), 'link');
  assert.equal(e2e.nextFilter('link', 'file'), 'file');
  assert.equal(e2e.nextFilter('link', 'link'), 'all');
  assert.equal(e2e.nextFilter('link', 'all'), 'all');
  assert.equal(e2e.nextFilter('all', 'all'), 'all');
});

test('filterEmptyText names the type, and says pinned in the Pinned tab', () => {
  assert.equal(e2e.filterEmptyText('text', false), 'No text yet');
  assert.equal(e2e.filterEmptyText('link', false), 'No links yet');
  assert.equal(e2e.filterEmptyText('image', false), 'No images yet');
  assert.equal(e2e.filterEmptyText('file', false), 'No files yet');
  assert.equal(e2e.filterEmptyText('text', true), 'No pinned text');
  assert.equal(e2e.filterEmptyText('link', true), 'No pinned links');
  assert.equal(e2e.filterEmptyText('image', true), 'No pinned images');
  assert.equal(e2e.filterEmptyText('file', true), 'No pinned files');
});
