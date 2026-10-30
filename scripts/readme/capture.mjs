// Seeds a local PrivatePaste server with the demo people and history, screenshots the web UI and composes the README hero.
// Run by build.ps1 (see README.md next to this file); expects the desktop and Android shots already in out/.
//
//   node capture.mjs --url http://localhost:5311 --password dev   (the owner signs in as "owner")
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
// The first person in demo.json is the owner, who is renamed from "owner"; every other one is added with a password.
const people = [...new Set(demo.map((c) => c.user))];
const passwords = Object.fromEntries(people.map((name, i) => [name, i === 0 ? password : 'demo-password-1']));
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

// 3. Seed the demo history, oldest first, as backfilled clips so each keeps its age. One sign-in per device
//    (the server allows 5 sign-ins a minute per IP, and the web sign-in above took one: keep to 4 devices).
const tokens = {};
for (const device of [...new Set(demo.map((c) => `${c.device}\n${c.platform}\n${c.user}`))]) {
  const [deviceName, platform, username] = device.split('\n');
  const res = await fetch(`${url}/api/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ username, password: passwords[username], deviceName, platform }),
  });
  if (!res.ok) throw new Error(`sign-in as ${username} on ${deviceName}: HTTP ${res.status}`);
  tokens[device] = (await res.json()).token;
}
for (const clip of [...demo].reverse()) {
  const auth = { authorization: `Bearer ${tokens[`${clip.device}\n${clip.platform}\n${clip.user}`]}` };
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

// 4. The web history, dark and light, at 2x.
{
  for (const scheme of ['dark', 'light']) {
    await web.emulateMedia({ colorScheme: scheme });
    await web.goto(`${url}/`);
    await web.waitForLoadState('networkidle');
    await web.screenshot({ path: join(out, `web-${scheme}.png`) });
  }
  await web.context().close();
}

// 5. The hero, from the web, desktop popup and Android shots, with transparent rounded corners.
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
