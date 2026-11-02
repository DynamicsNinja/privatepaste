// chrome.runtime messages are JSON, so bytes (thumbnails, downloads) cross between the offscreen document and the popup
// as base64. Chunked, so a 25 MB file doesn't overflow String.fromCharCode's argument limit.

const CHUNK = 0x8000;

export function toBase64(bytes) {
  let binary = '';
  for (let i = 0; i < bytes.length; i += CHUNK) binary += String.fromCharCode.apply(null, bytes.subarray(i, i + CHUNK));
  return btoa(binary);
}

export function fromBase64(text) {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
