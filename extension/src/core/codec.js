// Seals what was copied here for upload and opens what the server sends, through the shared crypto (e2e.js, copied into
// the build as js/e2e.js). The server sees only the kind, the keyed hash, the size and ciphertext. Never log what goes in
// or comes out.

import {
  contentHashText, contentHashPixels, keyedHash, sealPayload, openPayload, sealSmall, encryptBlob, payloadFiles,
} from '../js/e2e.js';
import { classifyText } from './classify.js';
import { pngInfo, pngToRgba, makeThumb, MAX_PIXELS } from './image.js';

export { contentHashText, contentHashPixels };

/** The server's defaults, used until /api/server-info says otherwise (fileMb is missing on older servers). */
export const DEFAULT_LIMITS = Object.freeze({ textKb: 1024, imageMb: 20, fileMb: 25 });
export const KNOWN_KINDS = Object.freeze(['text', 'link', 'image', 'file']);

export const TOO_MANY_PIXELS = "Images over 50 megapixels aren't synced.";
/** "Clip too large to sync (text over 1 MB).", with the server's limit (the desktop wording). */
export const textTooLargeMessage = textKb =>
  `Clip too large to sync (text over ${textKb % 1024 === 0 ? `${textKb / 1024} MB` : `${textKb} KB`}).`;
export const imageTooLargeMessage = imageMb => `Images over ${imageMb} MB aren't synced.`;

const utf8 = new TextEncoder();
const normalizeText = text => text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
const sha256Hex = async bytes =>
  Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), b => b.toString(16).padStart(2, '0')).join('');

/**
 * Checks a local copy against the server's limits and kinds and hashes it: the cheap checks first, so oversized content is
 * never decoded. A skip carries a `fingerprint` (what the content is remembered as, so the same content read again is not
 * checked or reported again) and, for a size limit, the `message` to show.
 * @param {{text?: string, png?: Uint8Array}} item
 * @returns {Promise<{kind, contentHash, sizeBytes, text?, png?, width?, height?} | {skip: string, fingerprint?: string, message?: string}>}
 */
export async function inspectLocal(item, limits = DEFAULT_LIMITS, kinds = KNOWN_KINDS) {
  if (typeof item?.text === 'string') {
    if (item.text.trim() === '') return { skip: 'empty' };
    const text = normalizeText(item.text);
    const bytes = utf8.encode(text);
    if (bytes.length > limits.textKb * 1024)
      return { skip: 'tooLarge', message: textTooLargeMessage(limits.textKb), fingerprint: `oversize:${await sha256Hex(bytes)}` };
    const kind = classifyText(text);
    const contentHash = await contentHashText(text);
    if (!kinds.includes(kind)) return { skip: 'kind', fingerprint: contentHash };
    return { kind, contentHash, sizeBytes: bytes.length, text };
  }

  const png = item?.png;
  if (!(png instanceof Uint8Array)) return { skip: 'unreadable' };
  const info = pngInfo(png);
  if (!info) return { skip: 'unreadable', fingerprint: `unreadable:${await sha256Hex(png)}` };
  if (!kinds.includes('image')) return { skip: 'kind', fingerprint: `kind:${await sha256Hex(png)}` };
  if (png.length > limits.imageMb * 1024 * 1024)
    return { skip: 'tooLarge', message: imageTooLargeMessage(limits.imageMb), fingerprint: `oversize:${await sha256Hex(png)}` };
  if (info.width * info.height > MAX_PIXELS)
    return { skip: 'tooLarge', message: TOO_MANY_PIXELS, fingerprint: `oversize:${await sha256Hex(png)}` };
  let decoded;
  try {
    decoded = await pngToRgba(png);
  } catch {
    return { skip: 'unreadable', fingerprint: `unreadable:${await sha256Hex(png)}` };
  }
  const contentHash = await contentHashPixels(decoded.rgba, decoded.width, decoded.height);
  return { kind: 'image', contentHash, sizeBytes: png.length, png, width: decoded.width, height: decoded.height };
}

/**
 * Seals an inspected copy as the parts of POST /api/clips. The PNG is uploaded as read from the clipboard (the browser
 * gives it upright, without EXIF), with a thumbnail fitting 256x256.
 * @returns {Promise<{meta: object, thumb?: Uint8Array, blobs: Uint8Array[]}>}
 */
export async function sealLocal(prepared, vault, { copiedAt, backfill }) {
  const image = prepared.kind === 'image';
  const meta = {
    kind: prepared.kind,
    keyId: vault.keyId,
    hash: await keyedHash(vault, prepared.contentHash),
    payload: await sealPayload(vault, image ? { width: prepared.width, height: prepared.height } : { text: prepared.text }),
    sizeBytes: prepared.sizeBytes,
    copiedAt: new Date(copiedAt).toISOString(),
    backfill: !!backfill,
  };
  if (!image) return { meta, blobs: [] };
  return {
    meta,
    thumb: await sealSmall(vault, 'thumb', await makeThumb(prepared.png)),
    blobs: [await encryptBlob(vault, prepared.png)],
  };
}

/** inspectLocal and sealLocal in one: `{meta, thumb?, blobs, contentHash}` or `{skip, ...}`. */
export async function encodeLocal(item, vault, limits = DEFAULT_LIMITS, kinds = KNOWN_KINDS,
  { copiedAt = Date.now(), backfill = false } = {}) {
  const prepared = await inspectLocal(item, limits, kinds);
  if (prepared.skip) return prepared;
  return { ...(await sealLocal(prepared, vault, { copiedAt, backfill })), contentHash: prepared.contentHash };
}

/**
 * Opens a clip from the server. Never throws for a bad clip: an unknown kind, another key, or a payload that doesn't open
 * or parse is `{kind: 'unknown'}`, shown as such and never put on the clipboard. File names come back safe (safeFileName).
 * @returns {Promise<{kind: string, text?: string, width?: number, height?: number, files?: {name, size, mime?}[]}>}
 */
export async function decodeClip(clip, vault) {
  const unknown = { kind: 'unknown' };
  if (!KNOWN_KINDS.includes(clip?.kind) || !vault || clip.keyId !== vault.keyId || typeof clip.payload !== 'string') return unknown;
  let payload;
  try {
    payload = await openPayload(vault, clip.payload);
  } catch {
    return unknown;   // not base64, tampered with, not JSON
  }
  if (payload === null || typeof payload !== 'object') return unknown;
  switch (clip.kind) {
    case 'text':
    case 'link':
      return typeof payload.text === 'string' ? { kind: clip.kind, text: payload.text } : unknown;
    case 'image':
      return { kind: 'image', width: payload.width, height: payload.height };
    default: {
      // One described file per blob, each with a name and a size, or it is not a clip this app can open.
      const raw = payload.files;
      if (!Array.isArray(raw) || !raw.every(f => typeof f?.name === 'string' && Number.isSafeInteger(f?.size) && f.size >= 0))
        return unknown;
      const files = payloadFiles(payload, clip.blobCount);
      if (!files) return unknown;
      return { kind: 'file', files: files.map((f, i) => (typeof raw[i].mime === 'string' ? { ...f, mime: raw[i].mime } : f)) };
    }
  }
}
