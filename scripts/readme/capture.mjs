// Seeds a local PrivatePaste server with the demo history, screenshots the web UI and composes the README hero.
// Run by build.ps1 (see README.md next to this file); expects the desktop and Android shots already in out/.
//
//   node capture.mjs --url http://localhost:5311 --password dev
import { chromium } from 'playwright';
import { readFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, 'out');
const images = join(here, '..', '..', 'docs', 'images');
const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const url = arg('url', 'http://localhost:5311').replace(/\/$/, '');
const password = arg('password', 'dev');
const demo = JSON.parse(readFileSync(join(here, 'demo.json'), 'utf8')).clips;
mkdirSync(out, { recursive: true });

// Installed Edge first, so no browser download is needed; Playwright's own Chromium otherwise.
const browser = await chromium.launch({ channel: 'msedge' }).catch(() => chromium.launch());
const page = async (options) => (await browser.newContext(options)).newPage();
const fileUrl = (name, query = '') => pathToFileURL(join(here, name)).href + query;

// 1. The image clip's picture.
{
  const p = await page({ viewport: { width: 1600, height: 900 } });
  await p.goto(fileUrl('photo.html'));
  await p.screenshot({ path: join(out, 'photo.png') });
  await p.context().close();
}

// 2. Seed the demo history, oldest first, as backfilled clips so each keeps its age. One sign-in per device
//    (the server allows 5 sign-ins a minute per IP, and the web UI below needs one more).
const tokens = {};
for (const device of [...new Set(demo.map((c) => `${c.device}\n${c.platform}`))]) {
  const [deviceName, platform] = device.split('\n');
  const res = await fetch(`${url}/api/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ password, deviceName, platform }),
  });
  if (!res.ok) throw new Error(`sign-in as ${deviceName}: HTTP ${res.status}`);
  tokens[device] = (await res.json()).token;
}
for (const clip of [...demo].reverse()) {
  const auth = { authorization: `Bearer ${tokens[`${clip.device}\n${clip.platform}`]}` };
  const copiedAt = new Date(Date.now() - clip.minutesAgo * 60_000).toISOString();
  let res;
  if (clip.kind === 'image') {
    const form = new FormData();
    form.append('file', new Blob([readFileSync(join(out, 'photo.png'))], { type: 'image/png' }), 'photo.png');
    form.append('copiedAt', copiedAt);
    form.append('backfill', 'true');
    res = await fetch(`${url}/api/clips/image`, { method: 'POST', headers: auth, body: form });
  } else {
    res = await fetch(`${url}/api/clips/text`, {
      method: 'POST',
      headers: { ...auth, 'content-type': 'application/json' },
      body: JSON.stringify({ text: clip.text, copiedAt, backfill: true }),
    });
  }
  if (!res.ok) throw new Error(`upload "${clip.text ?? clip.kind}": HTTP ${res.status}`);
  if (clip.pinned) {
    const pin = await fetch(`${url}/api/clips/${(await res.json()).id}/pin`, { method: 'POST', headers: auth });
    if (!pin.ok) throw new Error(`pin "${clip.text ?? clip.kind}": HTTP ${pin.status}`);
  }
}

// 3. The web history, dark and light, at 2x.
{
  const p = await page({ viewport: { width: 880, height: 860 }, deviceScaleFactor: 2 });
  await p.goto(`${url}/`);
  await p.locator('input[type=password]').fill(password);
  await Promise.all([p.waitForNavigation(), p.locator('input[type=password]').press('Enter')]);
  for (const scheme of ['dark', 'light']) {
    await p.emulateMedia({ colorScheme: scheme });
    await p.goto(`${url}/`);
    await p.waitForLoadState('networkidle');
    await p.screenshot({ path: join(out, `web-${scheme}.png`) });
  }
  await p.context().close();
}

// 4. The hero, from the web, desktop popup and Android shots, with transparent rounded corners.
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
