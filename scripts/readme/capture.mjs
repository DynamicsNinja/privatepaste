// Seeds a local PrivatePaste server with the demo people and history, screenshots the web UI and composes the README hero.
// Run by build.ps1 (see README.md next to this file); expects the desktop and Android shots already in out/.
//
//   node capture.mjs --url http://localhost:5311 --password dev   (the owner signs in as "owner")
//   node capture.mjs ... --web-only                               (stops after out/web-*.png; docs/images is untouched)
//
// The server is end-to-end encrypted: this sets up its vault with DEMO_PASSPHRASE, seals every demo clip with the web
// UI's own crypto module (wwwroot/js/e2e.js), and unlocks the web page with the passphrase before the shots.
import { chromium } from 'playwright';
import { readFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import * as e2e from '../../src/PrivatePaste.Server/wwwroot/js/e2e.js';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, 'out');
const images = join(here, '..', '..', 'docs', 'images');
const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const url = arg('url', 'http://localhost:5311').replace(/\/$/, '');
const password = arg('password', 'dev');
const webOnly = process.argv.includes('--web-only');
// Only ever used on the throwaway server: at least 12 characters and not in the common-passphrase list.
const DEMO_PASSPHRASE = 'readme demo vault passphrase';
// Every API call sends the protocol header; without it the clip and vault endpoints answer 426.
const PROTOCOL = { 'x-privatepaste-protocol': '2' };
const demo = JSON.parse(readFileSync(join(here, 'demo.json'), 'utf8')).clips;
// The first person in demo.json is the owner, who is renamed from "owner"; every other one is added with a password.
const people = [...new Set(demo.map((c) => c.user))];
const passwords = Object.fromEntries(people.map((name, i) => [name, i === 0 ? password : 'demo-password-1']));
mkdirSync(out, { recursive: true });

// Installed Edge first, so no browser download is needed; Playwright's own Chromium otherwise.
const browser = await chromium.launch({ channel: 'msedge' }).catch(() => chromium.launch());
const page = async (options) => (await browser.newContext(options)).newPage();
const fileUrl = (name, query = '') => pathToFileURL(join(here, name)).href + query;

// 1. The image clip's picture, plus what a client derives from it before uploading: the content hash of its RGBA
//    pixels (hashed here by e2e.js) and a PNG thumbnail that fits 256x256. The browser decodes the PNG, as the web
//    UI would, and hands back the pixels and the thumbnail as base64.
const photo = {};
{
  const p = await page({ viewport: { width: 1600, height: 900 } });
  await p.goto(fileUrl('photo.html'));
  photo.png = await p.screenshot({ path: join(out, 'photo.png') });
  const decoded = await p.evaluate(async (base64) => {
    const toBase64 = (bytes) => {
      let s = '';
      for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
      return btoa(s);
    };
    const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
    const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
    const { width, height } = bitmap;
    const full = new OffscreenCanvas(width, height).getContext('2d');
    full.drawImage(bitmap, 0, 0);
    const rgba = new Uint8Array(full.getImageData(0, 0, width, height).data.buffer);
    const scale = Math.min(1, 256 / width, 256 / height);
    const thumbCanvas = new OffscreenCanvas(Math.max(1, Math.round(width * scale)), Math.max(1, Math.round(height * scale)));
    thumbCanvas.getContext('2d').drawImage(bitmap, 0, 0, thumbCanvas.width, thumbCanvas.height);
    const thumb = new Uint8Array(await (await thumbCanvas.convertToBlob({ type: 'image/png' })).arrayBuffer());
    return { width, height, rgbaBase64: toBase64(rgba), thumbBase64: toBase64(thumb) };
  }, photo.png.toString('base64'));
  await p.context().close();
  photo.width = decoded.width;
  photo.height = decoded.height;
  photo.thumbBase64 = decoded.thumbBase64;
  photo.contentHash = await e2e.contentHashPixels(
    Uint8Array.from(Buffer.from(decoded.rgbaBase64, 'base64')), decoded.width, decoded.height);
}

// 2. The people. The owner (created from PP_PASSWORD, named "owner") is renamed and the others are added on the People
//    page, like a real owner would. This is the only web sign-in of the run; the screenshots below reuse its session.
const web = await page({ viewport: { width: 880, height: 860 }, deviceScaleFactor: 2 });
if (people.some(Boolean)) {
  await web.goto(`${url}/`);
  await web.locator('input[name=Name]').fill('owner');
  await web.locator('input[type=password]').fill(password);
  await Promise.all([web.waitForNavigation(), web.locator('input[type=password]').press('Enter')]);
  await web.goto(`${url}/people`);
  await web.locator('.person-card', { has: web.locator('.role') }).getByRole('link', { name: 'Rename' }).click();
  await web.locator('#rename-name').fill(people[0]);
  await Promise.all([web.waitForNavigation(), web.locator('#rename-name').press('Enter')]);
  for (const name of people.slice(1)) {
    await web.goto(`${url}/people`);
    await web.locator('.add-form input[name=name]').fill(name);
    await web.locator('.add-form input[name=password]').fill(passwords[name]);
    await Promise.all([web.waitForNavigation(), web.locator('.add-form button[type=submit]').click()]);
    if (!(await web.locator('.person-card', { hasText: name }).count())) throw new Error(`adding ${name} failed`);
  }
}

// 3. One sign-in per device (the server allows 5 sign-ins a minute per IP, and the web sign-in above took one: keep to
//    4 devices).
const tokens = {};
for (const device of [...new Set(demo.map((c) => `${c.device}\n${c.platform}\n${c.user}`))]) {
  const [deviceName, platform, username] = device.split('\n');
  const res = await fetch(`${url}/api/auth/login`, {
    method: 'POST',
    headers: { ...PROTOCOL, 'content-type': 'application/json' },
    body: JSON.stringify({ username, password: passwords[username], deviceName, platform }),
  });
  if (!res.ok) throw new Error(`sign-in as ${username} on ${deviceName}: HTTP ${res.status}`);
  tokens[device] = (await res.json()).token;
}
const authFor = (clip) => ({ ...PROTOCOL, authorization: `Bearer ${tokens[`${clip.device}\n${clip.platform}\n${clip.user}`]}` });

// 4. The vault, set up by one of the owner's devices, as an owner's app would on a fresh server.
const vault = await e2e.importVaultKey(crypto.getRandomValues(new Uint8Array(32)));
{
  const ownerClip = demo.find((c) => c.user === people[0]);
  const res = await fetch(`${url}/api/vault`, {
    method: 'PUT',
    headers: { ...authFor(ownerClip), 'content-type': 'application/json' },
    body: JSON.stringify(await e2e.wrapWithPassphrase(vault, DEMO_PASSPHRASE)),
  });
  if (!res.ok) throw new Error(`setting up the vault: HTTP ${res.status} (the server must be fresh)`);
}

// 5. The demo history, oldest first, as backfilled clips so each keeps its age, sealed like the apps seal them.
for (const clip of [...demo].reverse()) {
  const copiedAt = new Date(Date.now() - clip.minutesAgo * 60_000).toISOString();
  const form = new FormData();
  let meta;
  if (clip.kind === 'image') {
    meta = {
      kind: 'image',
      hash: await e2e.keyedHash(vault, photo.contentHash),
      payload: await e2e.sealPayload(vault, { width: photo.width, height: photo.height }),
      sizeBytes: photo.png.length,
    };
    form.append('thumb', new Blob([await e2e.sealSmall(vault, 'thumb', Uint8Array.from(Buffer.from(photo.thumbBase64, 'base64')))]));
    form.append('blob0', new Blob([await e2e.encryptBlob(vault, new Uint8Array(photo.png))]));
  } else {
    meta = {
      kind: clip.kind,
      hash: await e2e.keyedHash(vault, await e2e.contentHashText(clip.text)),
      payload: await e2e.sealPayload(vault, { text: clip.text }),
      sizeBytes: Buffer.byteLength(clip.text, 'utf8'),
    };
  }
  form.append('meta', JSON.stringify({ ...meta, keyId: vault.keyId, copiedAt, backfill: true }));
  const res = await fetch(`${url}/api/clips`, { method: 'POST', headers: authFor(clip), body: form });
  if (!res.ok) throw new Error(`upload "${clip.text ?? clip.kind}": HTTP ${res.status} ${await res.text()}`);
  if (clip.pinned) {
    const pin = await fetch(`${url}/api/clips/${(await res.json()).id}/pin`, { method: 'POST', headers: authFor(clip) });
    if (!pin.ok) throw new Error(`pin "${clip.text ?? clip.kind}": HTTP ${pin.status}`);
  }
}

// 6. The web history, dark and light, at 2x. Unlocked once: the key stays in the tab's sessionStorage across reloads.
//    Waits for every row, the Lock button in the top bar and the decrypted thumbnails before each shot.
{
  const ready = async () => {
    await web.locator('#clips > li.clip').nth(demo.length - 1).waitFor();
    await web.locator('#lock').waitFor({ state: 'visible' });
    await web.waitForFunction(() => [...document.querySelectorAll('#clips img.thumb')]
      .every((img) => img.complete && img.naturalWidth > 0));
    await web.waitForLoadState('networkidle');
  };
  await web.goto(`${url}/`);
  await web.locator('#unlock-passphrase').fill(DEMO_PASSPHRASE);
  await web.locator('#unlock-passphrase').press('Enter');
  await ready();
  for (const scheme of ['dark', 'light']) {
    await web.emulateMedia({ colorScheme: scheme });
    await web.goto(`${url}/`);
    await ready();
    await web.screenshot({ path: join(out, `web-${scheme}.png`) });
  }
  await web.context().close();
}

if (webOnly) {
  await browser.close();
  console.log(`Wrote out/web-dark.png and web-light.png (in ${out})`);
  process.exit(0);
}

// 7. The hero, from the web, desktop popup and Android shots, with transparent rounded corners.
for (const name of ['readme-popup-dark.png', 'readme-popup-light.png', 'readme-history-dark.png', 'readme-history-light.png']) {
  if (!existsSync(join(out, name))) throw new Error(`missing out/${name}: run the desktop and Android steps first`);
}
{
  const p = await page({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1.5 });
  for (const theme of ['dark', 'light']) {
    await p.goto(fileUrl('hero.html', `?t=${theme}`));
    await p.waitForLoadState('networkidle');
    await p.screenshot({ path: join(images, `hero-${theme}.png`), omitBackground: true });
  }
  await p.context().close();
}

await browser.close();
console.log(`Wrote docs/images/hero-dark.png and hero-light.png (shots in ${out})`);
