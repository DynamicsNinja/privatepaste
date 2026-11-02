import './support/register.mjs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const { pngToRgba, pngInfo, encodePng, makeThumb } = await import('../src/core/image.js');
const { contentHashPixels } = await import('../src/core/codec.js');

const vectors = JSON.parse(readFileSync(new URL('../../docs/e2e-test-vectors.json', import.meta.url), 'utf8'));
const hex = h => Uint8Array.from(Buffer.from(h, 'hex'));

for (const c of vectors.pngHash.cases) {
  test(`decodes the ${c.name} vector to the pixels .NET hashes`, async () => {
    const { rgba, width, height } = await pngToRgba(hex(c.png));
    assert.equal(width, c.width);
    assert.equal(height, c.height);
    assert.equal(rgba.length, width * height * 4);
    assert.equal(await contentHashPixels(rgba, width, height), c.contentHash);
  });
}

// The pixel cases of ContentHashTests.cs, through a PNG made here (compressed, unlike the vectors).
for (const [pixels, expected] of [
  [[255, 0, 0, 255], '8d833d22068fdf91eecbd0d310208548d1f9418c6513c572a7c7d01fc696c49a'],
  [[1, 2, 3, 4], null],
]) {
  test(`a 1x1 PNG of ${JSON.stringify(pixels)} hashes as ContentHash.ForPixels`, async () => {
    const png = await encodePng(Uint8Array.from(pixels), 1, 1);
    const { rgba } = await pngToRgba(png);
    assert.deepEqual([...rgba], pixels);
    const hash = await contentHashPixels(rgba, 1, 1);
    if (expected) assert.equal(hash, expected);
  });
}

test('encode then decode keeps every pixel, alpha included', async () => {
  const w = 37, h = 11;
  const rgba = Uint8Array.from({ length: w * h * 4 }, (_, i) => (i * 7919 + (i >> 3)) & 255);
  const back = await pngToRgba(await encodePng(rgba, w, h));
  assert.equal(back.width, w);
  assert.equal(back.height, h);
  assert.deepEqual(back.rgba, rgba);
});

test('pngInfo reads the size from the header alone', async () => {
  const png = await encodePng(new Uint8Array(3 * 2 * 4), 3, 2);
  assert.deepEqual(pngInfo(png), { width: 3, height: 2 });
  assert.equal(pngInfo(new Uint8Array([1, 2, 3])), null);
});

test('a broken PNG is refused', async () => {
  const png = await encodePng(new Uint8Array(4 * 4 * 4), 4, 4);
  await assert.rejects(() => pngToRgba(png.subarray(0, png.length - 20)));
  await assert.rejects(() => pngToRgba(new TextEncoder().encode('not a png at all')));
  const corrupt = Uint8Array.from(png);
  corrupt[20] ^= 0xff;   // inside IHDR: the CRC no longer matches
  await assert.rejects(() => pngToRgba(corrupt));
});

test('a small image is its own thumbnail', async () => {
  const png = await encodePng(new Uint8Array(10 * 20 * 4).fill(200), 10, 20);
  assert.equal(await makeThumb(png), png);
});

test('a large image gets a thumbnail fitting 256x256 with its aspect ratio', async () => {
  const w = 1000, h = 400;
  const rgba = new Uint8Array(w * h * 4);
  for (let i = 0; i < w * h; i++) rgba.set([255, 0, 0, 255], i * 4);
  const thumb = await pngToRgba(await makeThumb(await encodePng(rgba, w, h)));
  assert.equal(thumb.width, 256);
  assert.equal(thumb.height, 102);
  assert.deepEqual([...thumb.rgba.subarray(0, 4)], [255, 0, 0, 255]);
});

test('thumbnail averaging does not bleed the colour of transparent pixels', async () => {
  const w = 512, h = 2;
  const rgba = new Uint8Array(w * h * 4);
  for (let i = 0; i < w * h; i++) rgba.set(i % 2 ? [255, 255, 255, 0] : [0, 0, 255, 255], i * 4);
  const thumb = await pngToRgba(await makeThumb(await encodePng(rgba, w, h)));
  assert.deepEqual([...thumb.rgba.subarray(0, 4)], [0, 0, 255, 128]);
});
