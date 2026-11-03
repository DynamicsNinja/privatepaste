// Builds the Edge Add-ons listing images into extension/store/images/, run by hand:
//
//   node extension/store/make-images.mjs [--keep-out]
//
// It starts a LOCAL throwaway server (PP_PASSWORD=dev, a temporary PP_DATA_DIR, a free localhost port), seeds it with a
// made-up history from three made-up devices (sealed with the web UI's own crypto, as the apps seal them), loads a
// FAKE-clipboard build of the extension into Edge and drives popup.html: sign in, unlock, history, pinned, settings.
// Those 2x shots land in extension/store/out/ (git-ignored), and frame.html puts each on a Night vault background with a
// caption. Never the live server, never the OS clipboard. Needs Playwright from scripts/readme (`npm ci` there once)
// and the .NET SDK. Four sign-ins (three devices and the extension): under the server's 5 a minute.

import { createHash, randomBytes } from 'node:crypto';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import * as e2e from '../../src/PrivatePaste.Server/wwwroot/js/e2e.js';
import { buildFakeExtension, chromium, launchEdge, PROTOCOL, repo, startServer, waitFor } from '../test/helpers/local.mjs';

const here = fileURLToPath(new URL('.', import.meta.url));
const out = join(here, 'out');
const images = join(here, 'images');
mkdirSync(out, { recursive: true });
mkdirSync(images, { recursive: true });

// Only ever used on the throwaway server.
const PASSPHRASE = 'store listing demo vault';
const DEVICES = {
  laptop: { deviceName: 'Work laptop', platform: 'windows' },
  phone: { deviceName: 'Pixel 9', platform: 'android' },
  mac: { deviceName: 'MacBook Air', platform: 'macos' },
};
// Newest first. Made up: no real person, address or account in any of it.
const CLIPS = [
  { device: 'laptop', kind: 'text', text: 'Meeting moved to 15:30, room 4B', minutesAgo: 1, pinned: true },
  { device: 'phone', kind: 'link', text: 'https://github.com/DynamicsNinja/privatepaste', minutesAgo: 7 },
  { device: 'phone', kind: 'image', minutesAgo: 26 },
  { device: 'mac', kind: 'file', name: 'trip-itinerary.pdf', size: 184_320, minutesAgo: 48 },
  { device: 'phone', kind: 'text', text: 'Parcel 1Z 999 AA1 01 2345 6784 arrives Thursday', minutesAgo: 130, pinned: true },
  { device: 'laptop', kind: 'text', text: 'docker compose pull && docker compose up -d', minutesAgo: 190 },
  { device: 'mac', kind: 'text', text: 'Table for 4 at 19:30 on Friday', minutesAgo: 1500, pinned: true },
];

const work = mkdtempSync(join(tmpdir(), 'pp-ext-store-'));
const step = name => console.log(`- ${name}`);
let server;
let ctx;
let browser;
try {
  server = await startServer({ dataDir: join(work, 'data') });
  const { url } = server;
  if (/ficdev\.xyz/i.test(url)) throw new Error('Never run this against the live server.');

  // Frames and the photo are plain pages: headless Edge.
  browser = await chromium.launch({ channel: 'msedge' });

  step('the photo clip');
  const photo = {};
  {
    const p = await browser.newPage({ viewport: { width: 1600, height: 900 } });
    await p.goto(pathToFileURL(join(repo, 'scripts', 'readme', 'photo.html')).href);
    photo.png = await p.screenshot();
    const decoded = await p.evaluate(async base64 => {
      const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
      const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
      const { width, height } = bitmap;
      const full = new OffscreenCanvas(width, height).getContext('2d');
      full.drawImage(bitmap, 0, 0);
      const rgba = new Uint8Array(full.getImageData(0, 0, width, height).data.buffer);
      const scale = Math.min(1, 256 / width, 256 / height);
      const thumb = new OffscreenCanvas(Math.round(width * scale), Math.round(height * scale));
      thumb.getContext('2d').drawImage(bitmap, 0, 0, thumb.width, thumb.height);
      const toBase64 = b => { let s = ''; for (let i = 0; i < b.length; i += 0x8000) s += String.fromCharCode(...b.subarray(i, i + 0x8000)); return btoa(s); };
      const png = new Uint8Array(await (await thumb.convertToBlob({ type: 'image/png' })).arrayBuffer());
      return { width, height, rgba: toBase64(rgba), thumb: toBase64(png) };
    }, photo.png.toString('base64'));
    await p.close();
    Object.assign(photo, decoded);
    photo.hash = await e2e.contentHashPixels(Uint8Array.from(Buffer.from(decoded.rgba, 'base64')), decoded.width, decoded.height);
  }

  step('three devices sign in and the owner sets up encryption');
  const tokens = {};
  for (const [key, device] of Object.entries(DEVICES)) {
    const res = await fetch(`${url}/api/auth/login`, {
      method: 'POST',
      headers: { ...PROTOCOL, 'content-type': 'application/json' },
      body: JSON.stringify({ username: 'owner', password: 'dev', ...device }),
    });
    if (!res.ok) throw new Error(`sign-in on ${device.deviceName}: HTTP ${res.status}`);
    tokens[key] = (await res.json()).token;
  }
  const auth = key => ({ ...PROTOCOL, authorization: `Bearer ${tokens[key]}` });
  const vault = await e2e.importVaultKey(crypto.getRandomValues(new Uint8Array(32)));
  {
    const res = await fetch(`${url}/api/vault`, {
      method: 'PUT',
      headers: { ...auth('laptop'), 'content-type': 'application/json' },
      body: JSON.stringify(await e2e.wrapWithPassphrase(vault, PASSPHRASE)),
    });
    if (!res.ok) throw new Error(`vault: HTTP ${res.status}`);
  }

  step('the history, oldest first, as backfills so each keeps its age');
  for (const clip of [...CLIPS].reverse()) {
    const form = new FormData();
    let meta;
    if (clip.kind === 'image') {
      meta = {
        kind: 'image',
        hash: await e2e.keyedHash(vault, photo.hash),
        payload: await e2e.sealPayload(vault, { width: photo.width, height: photo.height }),
        sizeBytes: photo.png.length,
      };
      form.append('thumb', new Blob([await e2e.sealSmall(vault, 'thumb', Uint8Array.from(Buffer.from(photo.thumb, 'base64')))]));
      form.append('blob0', new Blob([await e2e.encryptBlob(vault, new Uint8Array(photo.png))]));
    } else if (clip.kind === 'file') {
      const bytes = new Uint8Array(randomBytes(clip.size));
      const entry = { name: clip.name, size: bytes.length, sha256Hex: createHash('sha256').update(bytes).digest('hex') };
      meta = {
        kind: 'file',
        hash: await e2e.keyedHash(vault, await e2e.contentHashFiles([entry])),
        payload: await e2e.sealPayload(vault, { files: [{ name: clip.name, size: bytes.length, mime: 'application/pdf' }] }),
        sizeBytes: bytes.length,
      };
      form.append('blob0', new Blob([await e2e.encryptBlob(vault, bytes)]));
    } else {
      meta = {
        kind: clip.kind,
        hash: await e2e.keyedHash(vault, await e2e.contentHashText(clip.text)),
        payload: await e2e.sealPayload(vault, { text: clip.text }),
        sizeBytes: Buffer.byteLength(clip.text, 'utf8'),
      };
    }
    const copiedAt = new Date(Date.now() - clip.minutesAgo * 60_000).toISOString();
    form.append('meta', JSON.stringify({ ...meta, keyId: vault.keyId, copiedAt, backfill: true }));
    const res = await fetch(`${url}/api/clips`, { method: 'POST', headers: auth(clip.device), body: form });
    if (!res.ok) throw new Error(`upload ${clip.kind}: HTTP ${res.status} ${await res.text()}`);
    if (clip.pinned) {
      const pin = await fetch(`${url}/api/clips/${(await res.json()).id}/pin`, { method: 'POST', headers: auth(clip.device) });
      if (!pin.ok) throw new Error(`pin: HTTP ${pin.status}`);
    }
  }

  step('the extension, with the fake clipboard');
  const ext = join(work, 'ext');
  buildFakeExtension(ext);
  ctx = await launchEdge(join(work, 'profile'), ext, { viewport: { width: 500, height: 900 }, deviceScaleFactor: 2 });
  let [sw] = ctx.serviceWorkers();
  sw ??= await ctx.waitForEvent('serviceworker');
  const id = new URL(sw.url()).host;

  // popup.html in a tab, drawn as the toolbar popup draws it (no tab margin) and without the thin scrollbar.
  const popup = await ctx.newPage();
  await popup.goto(`chrome-extension://${id}/popup.html`);
  const until = async (what, fn, arg, ms = 30000) => waitFor(what, () => popup.evaluate(fn, arg), ms);
  const shoot = async (name, scheme) => {
    await popup.emulateMedia({ colorScheme: scheme });
    await popup.evaluate(() => {
      document.body.classList.remove('in-tab');
      document.getElementById('scroller').style.scrollbarWidth = 'none';
      document.activeElement?.blur();
    });
    await popup.evaluate(() => document.fonts.ready);
    await popup.waitForTimeout(400);
    await popup.locator('body').screenshot({ path: join(out, name) });
  };

  step('sign-in');
  await popup.waitForSelector('#view-signin:not([hidden])', { timeout: 20000 });
  await popup.fill('#server', 'https://paste.example.com');
  await popup.fill('#username', 'owner');
  await popup.fill('#password', 'correct horse');
  await shoot('signin-dark.png', 'dark');

  await popup.fill('#server', url);
  await popup.fill('#password', 'dev');
  await popup.click('#signin-form button[type=submit]');
  await popup.waitForSelector('#view-unlock:not([hidden])', { timeout: 30000 });
  await popup.fill('#passphrase', PASSPHRASE);
  await popup.click('#unlock-form button[type=submit]');
  await popup.waitForSelector('#view-history:not([hidden])', { timeout: 30000 });

  step('history');
  await until('every row', n => document.querySelectorAll('#clips .clip').length === n, CLIPS.length);
  await until('the thumbnail', () => [...document.querySelectorAll('#clips img.thumb')].every(i => i.complete && i.naturalWidth > 0)
    && document.querySelectorAll('#clips img.thumb').length === 1);
  await until('Syncing', () => document.getElementById('status-text').textContent === 'Syncing');
  await shoot('history-dark.png', 'dark');
  await shoot('history-light.png', 'light');

  step('pinned');
  await popup.click('.tab[data-tab="pinned"]');
  await until('the pinned rows', n => document.querySelectorAll('.clip:not([hidden])').length === n, CLIPS.filter(c => c.pinned).length);
  await shoot('pinned-light.png', 'light');
  await popup.click('.tab[data-tab="history"]');

  step('settings');
  await popup.click('#open-settings');
  await popup.waitForSelector('#view-settings:not([hidden])');
  // The server row shows the throwaway localhost address; a listing shows the kind of address people really use.
  // The popup redraws every 2 s, so the text is put back whenever it does.
  await popup.evaluate(() => {
    const value = document.getElementById('settings-server');
    const fake = () => { if (value.textContent !== 'https://paste.example.com') value.textContent = 'https://paste.example.com'; };
    new MutationObserver(fake).observe(value, { childList: true, characterData: true, subtree: true });
    fake();
  });
  await shoot('settings-dark.png', 'dark');

  step('frames');
  const frame = await browser.newPage({ viewport: { width: 1400, height: 800 } });
  const render = async (query, file, size) => {
    await frame.setViewportSize(size);
    await frame.goto(`${pathToFileURL(join(here, 'frame.html')).href}?${new URLSearchParams(query)}`);
    await frame.waitForSelector('body[data-ready]');
    await frame.evaluate(async () => {
      await document.fonts.ready;
      await Promise.all([...document.images].map(i => i.complete ? null : new Promise(r => { i.onload = i.onerror = r; })));
    });
    await frame.locator('.stage, .logo').first().screenshot({ path: join(images, file), omitBackground: query.kind === 'logo' });
  };
  const shots = [
    ['screenshot-1-history.png', 'history-dark.png', 'Your clipboard history, one click away',
      'Copy on your laptop or phone, paste here. Text, links, images and files from all your devices, newest first.'],
    ['screenshot-2-pinned.png', 'pinned-light.png', 'Pin the clips you reuse',
      'Pinned clips have their own tab on every device and stay when the history is cleared.'],
    ['screenshot-3-settings.png', 'settings-dark.png', 'Pause any time',
      'Nothing is sent or received while paused. Signing out removes the keys and the history from this browser.'],
    ['screenshot-4-signin.png', 'signin-dark.png', 'Your own server, encrypted end to end',
      'Sign in to the PrivatePaste server you run. Clips are encrypted before they leave the browser.'],
  ];
  for (const [file, src, title, sub] of shots) await render({ kind: 'shot', src, title, sub }, file, { width: 1280, height: 800 });
  await render({ kind: 'tile-small' }, 'promo-small-440x280.png', { width: 440, height: 280 });
  await render({ kind: 'tile-large', src: 'history-dark.png' }, 'promo-large-1400x560.png', { width: 1400, height: 560 });
  await render({ kind: 'logo' }, 'store-logo-300.png', { width: 300, height: 300 });
  console.log(`Wrote ${images}`);
} finally {
  await ctx?.close().catch(() => {});
  await browser?.close().catch(() => {});
  await server?.stop().catch(() => {});
  rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
  if (!process.argv.includes('--keep-out')) rmSync(out, { recursive: true, force: true });
}
