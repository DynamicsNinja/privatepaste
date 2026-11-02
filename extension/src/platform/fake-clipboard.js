// In-memory stand-in for clipboard.js, swapped in by `build.mjs --fake-clipboard` for automated end-to-end tests, which
// must never touch the OS clipboard (the owner's desktop app syncs it). Tests drive it with runtime messages:
//   {type: 'test.clipboard.set', text?, pngBase64?}  a copy made in another app
//   {type: 'test.clipboard.get'}                      → {text?, pngBase64?}, what the extension last wrote
// Never part of a release build.

let current = {};

const toBase64 = bytes => btoa(Array.from(bytes, b => String.fromCharCode(b)).join(''));
const fromBase64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));

export async function readClipboard() {
  return { ...current };
}

export async function writeText(text) {
  current = { text };
  return true;
}

/** False, like the real one: images can't be written from the offscreen document. */
export async function writeImage(_png) {
  return false;
}

chrome.runtime.onMessage.addListener((message, _sender, reply) => {
  if (message?.type === 'test.clipboard.set') {
    current = {};
    if (message.text) current.text = message.text;
    if (message.pngBase64) current.png = fromBase64(message.pngBase64);
    reply({ ok: true });
  } else if (message?.type === 'test.clipboard.get') {
    const out = {};
    if (current.text) out.text = current.text;
    if (current.png) out.pngBase64 = toBase64(current.png);
    reply(out);
  }
});
