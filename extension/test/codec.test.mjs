import './support/register.mjs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const e2e = await import('../../src/PrivatePaste.Server/wwwroot/js/e2e.js');
const codec = await import('../src/core/codec.js');
const { encodePng } = await import('../src/core/image.js');

const vectors = JSON.parse(readFileSync(new URL('../../docs/e2e-test-vectors.json', import.meta.url), 'utf8'));
const hex = h => Uint8Array.from(Buffer.from(h, 'hex'));
const vault = await e2e.importVaultKey(hex(vectors.vaultKey.vk));
const limits = { textKb: 1, imageMb: 1, fileMb: 25 };

test('content hash and keyed hash of a text equal the vectors', async () => {
  const v = vectors.keyedHash;
  const out = await codec.encodeLocal({ text: v.text }, vault, limits);
  assert.equal(out.contentHash, v.contentHash);
  assert.equal(out.meta.hash, v.keyedHash);
  assert.equal(out.meta.keyId, vectors.vaultKey.keyId);
});

test('text is sealed with LF line ends, its UTF-8 size, and classified', async () => {
  const out = await codec.encodeLocal({ text: 'a\r\nb' }, vault, limits, codec.KNOWN_KINDS, { copiedAt: 0, backfill: true });
  assert.equal(out.meta.kind, 'text');
  assert.equal(out.meta.sizeBytes, 3);
  assert.equal(out.meta.copiedAt, '1970-01-01T00:00:00.000Z');
  assert.equal(out.meta.backfill, true);
  assert.deepEqual(out.blobs, []);
  assert.equal(out.thumb, undefined);
  assert.deepEqual(await e2e.openPayload(vault, out.meta.payload), { text: 'a\nb' });
  assert.equal((await codec.encodeLocal({ text: ' https://example.com ' }, vault, limits)).meta.kind, 'link');
});

test('a text over textKb x 1024 UTF-8 bytes is skipped as too large, with the message', async () => {
  const atLimit = await codec.encodeLocal({ text: 'x'.repeat(1024) }, vault, limits);
  assert.equal(atLimit.skip, undefined);
  const over = await codec.encodeLocal({ text: 'x'.repeat(1023) + 'š' }, vault, limits);
  assert.equal(over.skip, 'tooLarge');
  assert.equal(over.message, 'Clip too large to sync (text over 1 KB).');
  assert.match(over.fingerprint, /^oversize:[0-9a-f]{64}$/);
  assert.equal(codec.textTooLargeMessage(1024), 'Clip too large to sync (text over 1 MB).');
});

test('whitespace only is skipped', async () => {
  assert.equal((await codec.encodeLocal({ text: ' \n\t' }, vault, limits)).skip, 'empty');
});

test('a kind the server does not list is skipped', async () => {
  const link = await codec.encodeLocal({ text: 'https://example.com' }, vault, limits, ['text']);
  assert.equal(link.skip, 'kind');
  const png = await encodePng(new Uint8Array([1, 2, 3, 4]), 1, 1);
  assert.equal((await codec.encodeLocal({ png }, vault, limits, ['text', 'link'])).skip, 'kind');
});

test('an image is sealed with its size, a thumbnail and the PNG as blob 0', async () => {
  const png = await encodePng(new Uint8Array([255, 0, 0, 255, 0, 0, 255, 128]), 2, 1);
  const out = await codec.encodeLocal({ png }, vault, limits);
  assert.equal(out.meta.kind, 'image');
  assert.equal(out.meta.sizeBytes, png.length);
  assert.deepEqual(await e2e.openPayload(vault, out.meta.payload), { width: 2, height: 1 });
  assert.deepEqual(await e2e.decryptBlob(vault, out.blobs[0]), png);
  assert.deepEqual(await e2e.openSmall(vault, 'thumb', out.thumb), png);
  const rgba = Uint8Array.from([255, 0, 0, 255, 0, 0, 255, 128]);
  assert.equal(out.contentHash, await e2e.contentHashPixels(rgba, 2, 1));
});

test('an image over imageMb or 50 megapixels is skipped with the message; garbage is unreadable', async () => {
  const big = new Uint8Array(1024 * 1024 + 1);
  big.set(await encodePng(new Uint8Array(4), 1, 1));
  const over = await codec.encodeLocal({ png: big }, vault, limits);
  assert.equal(over.skip, 'tooLarge');
  assert.equal(over.message, "Images over 1 MB aren't synced.");

  const huge = await encodePng(new Uint8Array(4), 1, 1);
  new DataView(huge.buffer).setUint32(16, 10000);
  new DataView(huge.buffer).setUint32(20, 5001);
  const pixels = await codec.encodeLocal({ png: huge }, vault, limits);
  assert.equal(pixels.skip, 'tooLarge');
  assert.equal(pixels.message, "Images over 50 megapixels aren't synced.");

  assert.equal((await codec.encodeLocal({ png: new Uint8Array([1, 2, 3]) }, vault, limits)).skip, 'unreadable');
  assert.equal((await codec.encodeLocal({}, vault, limits)).skip, 'unreadable');
});

const wire = async (kind, payload, extra = {}) =>
  ({ id: 'c1', kind, keyId: vault.keyId, payload: await e2e.sealPayload(vault, payload), blobCount: 0, ...extra });

test('decodeClip opens text, link, image and file clips', async () => {
  assert.deepEqual(await codec.decodeClip(await wire('text', { text: 'hi' }), vault), { kind: 'text', text: 'hi' });
  assert.deepEqual(await codec.decodeClip(await wire('link', { text: 'https://x.y' }), vault), { kind: 'link', text: 'https://x.y' });
  assert.deepEqual(await codec.decodeClip(await wire('image', { width: 3, height: 4 }), vault), { kind: 'image', width: 3, height: 4 });
  const files = await codec.decodeClip(await wire('file', { files: [{ name: '../a.txt', size: 5, mime: 'text/plain' }] }, { blobCount: 1 }), vault);
  assert.deepEqual(files, { kind: 'file', files: [{ name: 'a.txt', size: 5, mime: 'text/plain' }] });
});

test('decodeClip of an unknown kind, another key or a bad payload gives unknown', async () => {
  assert.deepEqual(await codec.decodeClip(await wire('richtext', { text: 'x' }), vault), { kind: 'unknown' });
  assert.deepEqual(await codec.decodeClip({ ...(await wire('text', { text: 'x' })), keyId: '0000000000000000' }, vault), { kind: 'unknown' });
  assert.deepEqual(await codec.decodeClip({ ...(await wire('text', { text: 'x' })), payload: 'AAAA' }, vault), { kind: 'unknown' });
  assert.deepEqual(await codec.decodeClip(await wire('text', { nope: 1 }), vault), { kind: 'unknown' });
  assert.deepEqual(await codec.decodeClip(await wire('file', { files: [{ name: 'a', size: 1 }] }, { blobCount: 2 }), vault), { kind: 'unknown' });
  assert.deepEqual(await codec.decodeClip(await wire('file', { files: [{ name: 'a', size: -1 }] }, { blobCount: 1 }), vault), { kind: 'unknown' });
  assert.deepEqual(await codec.decodeClip(await wire('file', { files: [{ size: 1 }] }, { blobCount: 1 }), vault), { kind: 'unknown' });
});

test('decodeClip ignores payload fields it does not know', async () => {
  assert.deepEqual(await codec.decodeClip(await wire('text', { text: 'hi', html: '<b>hi</b>', later: { x: 1 } }), vault), { kind: 'text', text: 'hi' });
  assert.deepEqual(await codec.decodeClip(await wire('image', { width: 1, height: 2, exif: 6 }), vault), { kind: 'image', width: 1, height: 2 });
});
