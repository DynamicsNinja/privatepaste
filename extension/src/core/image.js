// PNG decoding and encoding without a canvas. A canvas premultiplies alpha, so semi-transparent pixels can come back
// rounded differently from what SkiaSharp (desktop) and Android decode, and the content hash, which is over
// unpremultiplied RGBA8888, would differ: echo prevention and dedup would break for such images. Decoding here is exact
// and pinned by the "pngHash" group of docs/e2e-test-vectors.json.

const SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
export const MAX_PIXELS = 50_000_000;
export const THUMB_SIZE = 256;

const GRAY = 0, RGB = 2, PALETTE = 3, GRAY_ALPHA = 4, RGBA = 6;
const CHANNELS = { [GRAY]: 1, [RGB]: 3, [PALETTE]: 1, [GRAY_ALPHA]: 2, [RGBA]: 4 };
const DEPTHS = { [GRAY]: [1, 2, 4, 8, 16], [RGB]: [8, 16], [PALETTE]: [1, 2, 4, 8], [GRAY_ALPHA]: [8, 16], [RGBA]: [8, 16] };
const ADAM7 = [[0, 0, 8, 8], [4, 0, 8, 8], [0, 4, 4, 8], [2, 0, 4, 4], [0, 2, 2, 4], [1, 0, 2, 2], [0, 1, 1, 2]];

const ascii = (bytes, at) => String.fromCharCode(bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]);
const u32 = (bytes, at) => ((bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3]) >>> 0;
const hasSignature = png => png.length >= 8 && SIGNATURE.every((b, i) => png[i] === b);

let crcTable;
function crc32(bytes, from, to) {
  if (!crcTable) {
    crcTable = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      crcTable[n] = c >>> 0;
    }
  }
  let crc = 0xffffffff;
  for (let i = from; i < to; i++) crc = crcTable[(crc ^ bytes[i]) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

async function transform(bytes, stream) {
  const out = new Blob([bytes]).stream().pipeThrough(stream);
  return new Uint8Array(await new Response(out).arrayBuffer());
}

/** The size from the PNG header, without decoding; null when it isn't a PNG. */
export function pngInfo(png) {
  if (!(png instanceof Uint8Array) || png.length < 33 || !hasSignature(png) || ascii(png, 12) !== 'IHDR') return null;
  return { width: u32(png, 16), height: u32(png, 20) };
}

function readChunks(png) {
  if (!hasSignature(png)) throw new Error('Not a PNG.');
  const chunks = [];
  let at = 8;
  while (true) {
    if (at + 12 > png.length) throw new Error('The PNG is truncated.');
    const length = u32(png, at);
    const type = ascii(png, at + 4);
    const end = at + 8 + length;
    if (end + 4 > png.length) throw new Error('The PNG is truncated.');
    // Critical chunks (upper-case first letter) must be intact, as libpng demands; ancillary ones are skipped anyway.
    const critical = type.charCodeAt(0) < 0x61;
    if (critical && crc32(png, at + 4, end) !== u32(png, end)) throw new Error('The PNG is corrupt.');
    chunks.push({ type, data: png.subarray(at + 8, end) });
    at = end + 4;
    if (type === 'IEND') return chunks;
  }
}

/**
 * Decodes a PNG to RGBA8888, unpremultiplied, row-major without padding: the pixels ContentHash.ForPixels hashes. Gray
 * and palette images are expanded, tRNS becomes alpha, low bit depths scale to 0..255 and 16-bit samples keep their high
 * byte, as SkiaSharp does. No colour or gamma conversion. Throws for anything that isn't a complete, valid PNG, and for images
 * over 50 megapixels (checked before inflating).
 * @returns {Promise<{rgba: Uint8Array, width: number, height: number}>}
 */
export async function pngToRgba(png) {
  const chunks = readChunks(png);
  if (chunks[0].type !== 'IHDR' || chunks[0].data.length !== 13) throw new Error('The PNG has no header.');
  const h = chunks[0].data;
  const width = u32(h, 0), height = u32(h, 4), depth = h[8], colorType = h[9], interlaced = h[12] === 1;
  if (width === 0 || height === 0 || width > 0x7fffffff || height > 0x7fffffff) throw new Error('The PNG has no pixels.');
  if (!DEPTHS[colorType]?.includes(depth) || h[10] !== 0 || h[11] !== 0 || h[12] > 1) throw new Error('Unsupported PNG.');
  if (width * height > MAX_PIXELS) throw new Error('The image is over 50 megapixels.');

  let palette = null, trns = null;
  const idat = [];
  for (const c of chunks) {
    if (c.type === 'PLTE') palette = c.data;
    else if (c.type === 'tRNS') trns = c.data;
    else if (c.type === 'IDAT') idat.push(c.data);
  }
  if (colorType === PALETTE && !palette) throw new Error('The PNG has no palette.');
  if (idat.length === 0) throw new Error('The PNG has no image data.');

  const data = await transform(concat(idat), new DecompressionStream('deflate'));
  const pixel = pixelReader(colorType, depth, palette, trns);
  const bitsPerPixel = depth * CHANNELS[colorType];
  const bpp = Math.max(1, bitsPerPixel >> 3);
  const rgba = new Uint8Array(width * height * 4);

  let at = 0;
  const passes = interlaced ? ADAM7 : [[0, 0, 1, 1]];
  for (const [x0, y0, dx, dy] of passes) {
    const passWidth = width > x0 ? Math.ceil((width - x0) / dx) : 0;
    const passHeight = height > y0 ? Math.ceil((height - y0) / dy) : 0;
    if (passWidth === 0 || passHeight === 0) continue;
    const stride = Math.ceil((passWidth * bitsPerPixel) / 8);
    let prev = new Uint8Array(stride);
    for (let row = 0; row < passHeight; row++) {
      if (at + 1 + stride > data.length) throw new Error('The PNG is truncated.');
      const line = unfilter(data[at], data.subarray(at + 1, at + 1 + stride), prev, bpp);
      at += 1 + stride;
      const y = y0 + row * dy;
      for (let i = 0; i < passWidth; i++) pixel(line, i, rgba, (y * width + x0 + i * dx) * 4);
      prev = line;
    }
  }
  return { rgba, width, height };
}

function unfilter(type, filtered, prev, bpp) {
  const line = Uint8Array.from(filtered);
  const n = line.length;
  switch (type) {
    case 0: break;
    case 1: for (let i = bpp; i < n; i++) line[i] += line[i - bpp]; break;
    case 2: for (let i = 0; i < n; i++) line[i] += prev[i]; break;
    case 3:
      for (let i = 0; i < n; i++) line[i] += ((i >= bpp ? line[i - bpp] : 0) + prev[i]) >> 1;
      break;
    case 4:
      for (let i = 0; i < n; i++) {
        const a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
        const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
        line[i] += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
      }
      break;
    default: throw new Error('The PNG has an unknown filter.');
  }
  return line;
}

// 16-bit to 8-bit as SkiaSharp decodes it: the high byte, not rounded (pinned by the rgba16 vector).
const from16 = v => v >>> 8;

/** A function writing pixel i of an unfiltered line into rgba at offset o. */
function pixelReader(colorType, depth, palette, trns) {
  const channels = CHANNELS[colorType];
  const sample = depth === 16
    ? (line, i, c) => (line[(i * channels + c) * 2] << 8) | line[(i * channels + c) * 2 + 1]
    : depth === 8
      ? (line, i, c) => line[i * channels + c]
      : (line, i) => (line[(i * depth) >> 3] >> (8 - depth - ((i * depth) & 7))) & ((1 << depth) - 1);
  const to8 = depth === 16 ? from16 : depth === 8 ? v => v : v => (v * 255) / ((1 << depth) - 1);
  const key = (n) => (trns && trns.length >= 2 * n ? Array.from({ length: n }, (_, k) => (trns[2 * k] << 8) | trns[2 * k + 1]) : null);

  switch (colorType) {
    case GRAY: {
      const transparent = key(1);
      return (line, i, out, o) => {
        const v = sample(line, i, 0), g = to8(v);
        out[o] = g; out[o + 1] = g; out[o + 2] = g;
        out[o + 3] = transparent && v === transparent[0] ? 0 : 255;
      };
    }
    case RGB: {
      const transparent = key(3);
      return (line, i, out, o) => {
        const r = sample(line, i, 0), g = sample(line, i, 1), b = sample(line, i, 2);
        out[o] = to8(r); out[o + 1] = to8(g); out[o + 2] = to8(b);
        out[o + 3] = transparent && r === transparent[0] && g === transparent[1] && b === transparent[2] ? 0 : 255;
      };
    }
    case PALETTE:
      return (line, i, out, o) => {
        const index = sample(line, i, 0);
        if (index * 3 + 2 < palette.length) {
          out[o] = palette[index * 3]; out[o + 1] = palette[index * 3 + 1]; out[o + 2] = palette[index * 3 + 2];
        }   // an index past the palette stays black
        out[o + 3] = trns && index < trns.length ? trns[index] : 255;
      };
    case GRAY_ALPHA:
      return (line, i, out, o) => {
        const g = to8(sample(line, i, 0));
        out[o] = g; out[o + 1] = g; out[o + 2] = g; out[o + 3] = to8(sample(line, i, 1));
      };
    default:
      return (line, i, out, o) => {
        for (let c = 0; c < 4; c++) out[o + c] = to8(sample(line, i, c));
      };
  }
}

function concat(parts) {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
}

function chunk(type, data) {
  const out = new Uint8Array(12 + data.length);
  const view = new DataView(out.buffer);
  view.setUint32(0, data.length);
  for (let i = 0; i < 4; i++) out[4 + i] = type.charCodeAt(i);
  out.set(data, 8);
  view.setUint32(8 + data.length, crc32(out, 4, 8 + data.length));
  return out;
}

/** Encodes RGBA8888 (unpremultiplied) as an 8-bit RGBA PNG, choosing a filter per row. */
export async function encodePng(rgba, width, height) {
  const stride = width * 4;
  const raw = new Uint8Array((stride + 1) * height);
  const candidate = new Uint8Array(stride);
  let prev = new Uint8Array(stride);
  for (let y = 0; y < height; y++) {
    const line = rgba.subarray(y * stride, (y + 1) * stride);
    let best = Infinity;
    for (let type = 0; type <= 4; type++) {
      let score = 0;
      for (let i = 0; i < stride; i++) {
        const a = i >= 4 ? line[i - 4] : 0, b = prev[i], c = i >= 4 ? prev[i - 4] : 0;
        let predictor = 0;
        if (type === 1) predictor = a;
        else if (type === 2) predictor = b;
        else if (type === 3) predictor = (a + b) >> 1;
        else if (type === 4) {
          const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
          predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
        const v = (line[i] - predictor) & 0xff;
        candidate[i] = v;
        score += v < 128 ? v : 256 - v;
      }
      if (score < best) {
        best = score;
        raw[y * (stride + 1)] = type;
        raw.set(candidate, y * (stride + 1) + 1);
      }
    }
    prev = line;
  }
  const header = new Uint8Array(13);
  const view = new DataView(header.buffer);
  view.setUint32(0, width);
  view.setUint32(4, height);
  header[8] = 8;
  header[9] = RGBA;
  return concat([
    Uint8Array.from(SIGNATURE),
    chunk('IHDR', header),
    chunk('IDAT', await transform(raw, new CompressionStream('deflate'))),
    chunk('IEND', new Uint8Array(0)),
  ]);
}

/**
 * A PNG thumbnail fitting 256x256 (the image itself when it already fits), scaled by area averaging with alpha
 * weighting, so transparent pixels don't bleed their colour into the edges.
 */
export async function makeThumb(png) {
  const info = pngInfo(png);
  if (info && Math.max(info.width, info.height) <= THUMB_SIZE) return png;
  const { rgba, width, height } = await pngToRgba(png);
  const scale = THUMB_SIZE / Math.max(width, height);
  const tw = Math.min(THUMB_SIZE, Math.max(1, Math.round(width * scale)));
  const th = Math.min(THUMB_SIZE, Math.max(1, Math.round(height * scale)));
  const out = new Uint8Array(tw * th * 4);
  for (let ty = 0; ty < th; ty++) {
    const y0 = Math.floor((ty * height) / th), y1 = Math.max(y0 + 1, Math.floor(((ty + 1) * height) / th));
    for (let tx = 0; tx < tw; tx++) {
      const x0 = Math.floor((tx * width) / tw), x1 = Math.max(x0 + 1, Math.floor(((tx + 1) * width) / tw));
      let r = 0, g = 0, b = 0, a = 0;
      for (let y = y0; y < y1; y++) {
        for (let x = x0, i = (y * width + x0) * 4; x < x1; x++, i += 4) {
          const alpha = rgba[i + 3];
          r += rgba[i] * alpha; g += rgba[i + 1] * alpha; b += rgba[i + 2] * alpha; a += alpha;
        }
      }
      const o = (ty * tw + tx) * 4;
      const count = (y1 - y0) * (x1 - x0);
      out[o + 3] = Math.round(a / count);
      if (a > 0) {
        out[o] = Math.round(r / a); out[o + 1] = Math.round(g / a); out[o + 2] = Math.round(b / a);
      }
    }
  }
  return encodePng(out, tw, th);
}
