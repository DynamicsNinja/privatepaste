// Browser check of the web history page and Settings: setup, lock, unlock, rows decrypted and drawn safely, Copy, Pin,
// Delete, search, a reset noticed by an open tab, change passphrase, add a device by QR, reset encryption and the strict CSP. Run by hand (not in CI) against a FRESH local server, never the live one:
//
//   PP_DB_KEY=0123456789abcdef0123 PP_PASSWORD=dev PP_DATA_DIR=<empty temp dir> \
//     dotnet run --project src/PrivatePaste.Server --no-launch-profile --urls http://localhost:5303
//   node tests/web/history.playwright.mjs [--url http://localhost:5303] [--password dev]
//
// Uses Playwright from scripts/readme (run `npm ci` there once). With PP_SCREENSHOT_DIR set, it saves the setup,
// unlock, history, file clip, type filter, settings and dialog states in dark and light at desktop and phone widths. It signs in once.

import assert from 'node:assert/strict';
import { mkdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium } from '../../scripts/readme/node_modules/playwright/index.mjs';

const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const url = arg('url', 'http://localhost:5303');
const password = arg('password', 'dev');
const passphrase = 'purple walrus eats pancakes at dawn';
const changedPassphrase = 'orange heron naps beside the harbor';
const resetPassphrase = 'silver otter reads maps at noon';
const xss = '<img src=x onerror=window.__x=1>';
const shots = process.env.PP_SCREENSHOT_DIR;
if (shots) mkdirSync(shots, { recursive: true });

const sizes = { desktop: { width: 1100, height: 900 }, phone: { width: 390, height: 844 } };

const browser = await chromium.launch({ channel: 'msedge' }).catch(() => chromium.launch());
const context = await browser.newContext({ viewport: sizes.desktop, colorScheme: 'dark' });
await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: url });
// Every CSP violation and script error on any page load, so the check can fail on any.
const cspViolations = [];
const pageErrors = [];
await context.exposeBinding('__reportCsp', (_, violation) => cspViolations.push(violation));
await context.addInitScript(() => {
  document.addEventListener('securitypolicyviolation', e => window.__reportCsp(`${e.violatedDirective} ${e.blockedURI}`));
});
const page = await context.newPage();
const thumbRequests = [];
page.on('request', r => { if (/\/thumb$/.test(new URL(r.url()).pathname)) thumbRequests.push(r.url()); });
page.on('console', m => { if (/Content.Security.Policy/i.test(m.text())) cspViolations.push(m.text()); });
page.on('pageerror', e => pageErrors.push(e.message));

const visible = selector => page.locator(selector).isVisible();
const text = selector => page.locator(selector).innerText();
const step = name => console.log(`- ${name}`);

// page.waitForFunction evaluates a string in the page, which the strict CSP blocks; this polls with evaluate instead.
async function until(fn, arg, timeout = 30000) {
  const end = Date.now() + timeout;
  while (!(await page.evaluate(fn, arg))) {
    if (Date.now() > end) throw new Error(`Timed out waiting for ${fn}`);
    await page.waitForTimeout(100);
  }
}

async function shoot(state) {
  if (!shots) return;
  for (const scheme of ['dark', 'light']) {
    await page.emulateMedia({ colorScheme: scheme });
    // Buttons fade their colours over .15s; let the switch settle before the shot.
    await page.waitForTimeout(400);
    for (const [size, viewport] of Object.entries(sizes)) {
      await page.setViewportSize(viewport);
      await page.screenshot({ path: join(shots, `web-${state}-${scheme}-${size}.png`), });
    }
  }
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.setViewportSize(sizes.desktop);
}

// Uploads clips as a client would, sealed with this tab's key, through the cookie session.
async function upload(clips) {
  return page.evaluate(async clips => {
    const shell = document.getElementById('history');
    const e2e = await import(shell.dataset.e2e);
    const { api } = await import(shell.dataset.history);
    const raw = Uint8Array.from(atob(sessionStorage.getItem('pp.vault')), c => c.charCodeAt(0));
    const vault = await e2e.importVaultKey(raw);
    const ids = [];
    for (const clip of clips) {
      // meta first: the server takes the parts in any order, but this is the order the other clients send.
      const body = new FormData();
      if (clip.kind === 'image') {
        const canvas = new OffscreenCanvas(clip.width, clip.height);
        const g = canvas.getContext('2d');
        const gradient = g.createLinearGradient(0, 0, clip.width, clip.height);
        gradient.addColorStop(0, '#5B45D6');
        gradient.addColorStop(1, '#B7A5FF');
        g.fillStyle = gradient;
        g.fillRect(0, 0, clip.width, clip.height);
        const rgba = new Uint8Array(g.getImageData(0, 0, clip.width, clip.height).data.buffer);
        const png = new Uint8Array(await (await canvas.convertToBlob({ type: 'image/png' })).arrayBuffer());
        body.append('meta', JSON.stringify({
          kind: 'image', keyId: vault.keyId,
          hash: await e2e.keyedHash(vault, await e2e.contentHashPixels(rgba, clip.width, clip.height)),
          payload: await e2e.sealPayload(vault, { width: clip.width, height: clip.height }),
          sizeBytes: png.length, backfill: false,
        }));
        body.append('thumb', new Blob([await e2e.sealSmall(vault, 'thumb', png)]), 'thumb');
        body.append('blob0', new Blob([await e2e.encryptBlob(vault, png)]), 'blob0');
      } else if (clip.kind === 'file') {
        const sha = async b => [...new Uint8Array(await crypto.subtle.digest('SHA-256', b))].map(x => x.toString(16).padStart(2, '0')).join('');
        const contents = clip.files.map(f => new TextEncoder().encode(f.content));
        const entries = await Promise.all(clip.files.map(async (f, i) => ({ name: f.name, size: contents[i].length, sha256Hex: await sha(contents[i]) })));
        body.append('meta', JSON.stringify({
          kind: 'file', keyId: vault.keyId,
          hash: await e2e.keyedHash(vault, await e2e.contentHashFiles(entries)),
          payload: await e2e.sealPayload(vault, { files: clip.files.map((f, i) => ({ name: f.name, size: contents[i].length, mime: 'text/plain' })) }),
          sizeBytes: contents.reduce((n, c) => n + c.length, 0), backfill: false,
        }));
        for (const [i, c] of contents.entries()) body.append(`blob${i}`, new Blob([await e2e.encryptBlob(vault, c)]), `blob${i}`);
      } else {
        body.append('meta', JSON.stringify({
          kind: clip.kind, keyId: vault.keyId,
          hash: await e2e.keyedHash(vault, await e2e.contentHashText(clip.text)),
          payload: await e2e.sealPayload(vault, { text: clip.text }),
          sizeBytes: new TextEncoder().encode(clip.text).length, backfill: false,
        }));
      }
      const response = await api('/api/clips', { method: 'POST', body });
      if (!response.ok) throw new Error(`upload ${clip.kind}: HTTP ${response.status} ${await response.text()}`);
      ids.push((await response.json()).id);
    }
    return ids;
  }, clips);
}

const row = id => page.locator(`.clip[data-id="${id}"]`);

try {
  step('sign in');
  await page.goto(`${url}/login`);
  await page.fill('#Name', 'owner');
  await page.fill('#Password', password);
  await Promise.all([page.waitForURL(`${url}/`), page.click('button[type=submit]')]);

  step('a fresh server asks the owner to set up encryption');
  await page.waitForSelector('#setup:not([hidden])', { timeout: 15000 }).catch(() => {
    throw new Error('No setup panel: run this against a fresh server (an empty PP_DATA_DIR).');
  });
  assert.equal(await text('#setup h1'), 'Set up encryption');
  assert.match(await text('#setup'), /Use at least 12 characters\. A short sentence works well\./);
  assert.equal(await visible('#lock'), false);
  await shoot('setup');

  await page.fill('#setup-passphrase', 'too short');
  await page.fill('#setup-repeat', 'too short');
  await page.click('#setup-form button');
  await page.waitForSelector('#setup-error:not([hidden])');
  assert.equal(await text('#setup-error'), 'Use at least 12 characters. A short sentence works well.');
  await page.fill('#setup-passphrase', passphrase);
  await page.fill('#setup-repeat', passphrase + ' ');
  await page.click('#setup-form button');
  await until(() => document.getElementById('setup-error').textContent === "The passphrases don't match.");

  step('setup with a passphrase unlocks an empty history');
  await page.fill('#setup-repeat', passphrase);
  await page.click('#setup-form button');
  await page.waitForSelector('#clips-view:not([hidden])', { timeout: 30000 });
  await page.waitForSelector('#history-empty:not([hidden])');
  assert.equal(await text('#history-empty'), 'Nothing here yet. Copy something on a signed-in device and it shows up here.');
  assert.equal(await visible('#lock'), true);
  assert.ok(await page.evaluate(() => sessionStorage.getItem('pp.vault')));

  step('upload clips through the API');
  // Oldest first: one match only an older page holds, a page of filler, then the clips to look at.
  const [oldImageId, needleId] = await upload([
    { kind: 'image', width: 64, height: 48 },
    { kind: 'text', text: 'needle in an older page' },
  ]);
  await upload(Array.from({ length: 50 }, (_, i) => ({ kind: 'text', text: `filler clip ${i}` })));
  const [jsLinkId, imageId, linkId, xssId] = await upload([
    { kind: 'link', text: 'javascript:window.__y=1' },
    { kind: 'image', width: 320, height: 200 },
    { kind: 'link', text: 'https://example.com/docs?page=2' },
    { kind: 'text', text: xss },
  ]);

  step('reload: the tab still holds the key, so the history shows without a passphrase');
  await page.reload();
  await row(xssId).waitFor();
  assert.equal(await page.locator('#unlock').isHidden(), true);

  step('Lock forgets the key and asks for the passphrase');
  await page.click('#lock');
  await page.waitForSelector('#unlock:not([hidden])');
  assert.equal(await text('#unlock h1'), 'Unlock your history');
  assert.equal(await page.evaluate(() => sessionStorage.getItem('pp.vault')), null);
  assert.equal(await page.locator('.clip').count(), 0);
  assert.equal(await visible('#lock'), false);
  await page.reload();
  await page.waitForSelector('#unlock:not([hidden])');
  assert.equal(await page.locator('.clip').count(), 0);
  await shoot('unlock');

  step('a wrong passphrase is refused');
  await page.fill('#unlock-passphrase', 'not the passphrase at all');
  await page.click('#unlock-form button');
  await page.waitForSelector('#unlock-error:not([hidden])', { timeout: 30000 });
  assert.equal(await text('#unlock-error'), "That passphrase doesn't unlock this server's history.");

  step('unlock, and the rows are drawn as text');
  await page.fill('#unlock-passphrase', passphrase);
  await page.click('#unlock-form button');
  await row(xssId).waitFor({ timeout: 30000 });
  assert.equal(await row(xssId).locator('pre.text').innerText(), xss);
  assert.equal(await row(xssId).locator('img').count(), 0);
  assert.equal(await page.evaluate(() => window.__x), undefined);
  assert.equal(await page.evaluate(() => window.__y), undefined);
  assert.equal(await page.locator('.clip').count(), 50);
  assert.ok(await row(xssId).evaluate(el => el.classList.contains('newest')));
  // One person: no name chip; the device is this browser session.
  assert.equal(await page.locator('.clip .person-chip').count(), 0);
  assert.equal(await row(xssId).locator('.meta svg.platform').getAttribute('data-platform'), 'web');
  assert.match(await row(xssId).locator('.meta').innerText(), /^Web, just now$/);

  step('only http(s) links are anchors');
  const anchor = row(linkId).locator('.body a');
  assert.equal(await anchor.getAttribute('href'), 'https://example.com/docs?page=2');
  assert.equal(await anchor.getAttribute('rel'), 'noopener noreferrer');
  assert.equal(await anchor.getAttribute('target'), '_blank');
  assert.equal(await anchor.innerText(), 'https://example.com/docs?page=2');
  assert.equal(await row(jsLinkId).locator('a').count(), 0);
  assert.equal(await row(jsLinkId).locator('pre.text').innerText(), 'javascript:window.__y=1');

  step('the image thumbnail is decrypted into a blob: URL');
  const thumb = row(imageId).locator('img.thumb');
  await until(id => document.querySelector(`.clip[data-id="${id}"] img.thumb`)?.naturalWidth > 0, imageId);
  assert.match(await thumb.getAttribute('src'), /^blob:/);
  assert.equal(await thumb.getAttribute('alt'), 'Image clip 320 x 200');

  await shoot('history');

  step('Copy puts the decrypted text and image on the clipboard');
  await row(xssId).locator('button.copy').click();
  await row(xssId).locator('button.copy.done').waitFor();
  assert.equal(await page.evaluate(() => navigator.clipboard.readText()), xss);
  await row(imageId).locator('button.copy').click();
  await row(imageId).locator('button.copy.done').waitFor();
  const types = await page.evaluate(async () => (await navigator.clipboard.read()).flatMap(item => item.types));
  assert.ok(types.includes('image/png'), `clipboard holds ${types}`);

  step('a failed Pin shows on the button');
  await page.route('**/pin', route => route.fulfill({ status: 500, json: {} }));
  await row(linkId).locator('button.pin-toggle').click();
  await row(linkId).locator('button.pin-toggle.failed').waitFor();
  assert.equal(await row(linkId).locator('.pin-toggle .sr-only').innerText(), 'Pin failed');
  await page.unrouteAll();
  await row(linkId).locator('button.pin-toggle:not(.failed):not([disabled])').waitFor();
  assert.equal(await row(linkId).locator('.pin-toggle .sr-only').innerText(), 'Pin');

  step('Pin shows the marker; the Pinned tab lists it; Unpin there drops it');
  await row(linkId).locator('button.pin-toggle').click();
  await row(linkId).locator('button.pin-toggle[aria-pressed="true"]').waitFor();
  assert.equal(await row(linkId).locator('.pin-marker').isVisible(), true);
  assert.equal(await row(linkId).locator('.pin-toggle .sr-only').innerText(), 'Unpin');
  await page.click('.tabs a:has-text("Pinned")');
  await row(linkId).waitFor();
  assert.equal(await page.locator('.clip').count(), 1);
  assert.equal(await row(linkId).locator('.pin-marker').isVisible(), false);
  await row(linkId).locator('button.pin-toggle').click();
  await row(linkId).waitFor({ state: 'detached' });
  await page.waitForSelector('#history-empty:not([hidden])');
  assert.equal(await text('#history-empty'), 'No pinned clips. Pin a clip to keep it through Clear history and auto-delete.');
  await page.click('.tabs a:has-text("History")');
  await row(xssId).waitFor();

  step('Delete removes the row and the clip');
  await row(jsLinkId).locator('button.delete').click();
  await row(jsLinkId).waitFor({ state: 'detached' });
  assert.equal(await page.evaluate(async id => {
    const { api } = await import(document.getElementById('history').dataset.history);
    return (await api(`/api/clips/${id}`)).status;
  }, jsLinkId), 404);

  step('search filters the loaded rows, and "Search older clips" finds the rest');
  await page.fill('#search-input', 'NEEDLE');
  await page.waitForSelector('#history-empty:not([hidden])');
  assert.equal(await text('#history-empty'), 'No clips match that search.');
  assert.equal(await visible('#search-older'), true);
  assert.equal(await visible('#load-more'), false);
  await page.click('#search-older');
  await row(needleId).waitFor();
  assert.equal(await page.locator('.clip:visible').count(), 1);
  assert.equal(await visible('#search-older'), false);
  assert.equal(await row(needleId).evaluate(el => el.classList.contains('newest')), false);
  // The older image's row was loaded but never shown, so its thumbnail wasn't fetched.
  assert.ok(!thumbRequests.some(u => u.includes(oldImageId)), 'a hidden row fetched its thumbnail');
  await page.fill('#search-input', '');
  assert.ok(await page.locator('.clip:visible').count() > 50);
  assert.ok(!thumbRequests.some(u => u.includes(oldImageId)), 'an off-screen row fetched its thumbnail');
  await row(oldImageId).scrollIntoViewIfNeeded();
  await until(id => document.querySelector(`.clip[data-id="${id}"] img.thumb`)?.naturalWidth > 0, oldImageId);
  await page.evaluate(() => window.scrollTo(0, 0));

  step('a clip of a kind this page does not know');
  await page.route('**/api/clips?*', async route => {
    const response = await route.fetch();
    const body = await response.json();
    body.items.unshift({ ...body.items[0], id: '00000000-0000-4000-8000-000000000001', kind: 'hologram', payload: null });
    await route.fulfill({ response, json: body });
  });
  await page.reload();
  const unknown = row('00000000-0000-4000-8000-000000000001');
  await unknown.waitFor();
  assert.equal(await unknown.locator('.body .text').innerText(), 'Update PrivatePaste to see this clip');
  assert.equal(await unknown.locator('button.copy').count(), 0);
  assert.equal(await unknown.locator('button.pin-toggle').count(), 1);
  await page.unroute('**/api/clips?*');

  step('a file clip: row text, no Copy, search by file name, one download per file under the safe name');
  const [fileId] = await upload([{ kind: 'file', files: [
    { name: '..\\..\\report.txt', content: 'first file' }, { name: 'notes.txt', content: 'second file' }] }]);
  await page.reload();
  const fileRow = row(fileId);
  await fileRow.waitFor();
  assert.match(await fileRow.locator('.body .text').first().innerText(), /^report\.txt \+1 more . 21 B$/);
  assert.equal(await fileRow.locator('button.copy').count(), 0);
  assert.equal(await fileRow.locator('.kind svg').count(), 1);
  await page.fill('#search-input', 'NOTES');
  assert.equal(await fileRow.isVisible(), true);
  await page.fill('#search-input', '');
  for (const [i, name] of ['report.txt', 'notes.txt'].entries()) {
    const [download] = await Promise.all([
      page.waitForEvent('download'),
      fileRow.locator(`button[aria-label="Download ${name}"]`).click(),
    ]);
    assert.equal(download.suggestedFilename(), name);
    const text = readFileSync(await download.path(), 'utf8');
    assert.equal(text, i === 0 ? 'first file' : 'second file');
  }
  await shoot('history-file');

  step('type chips: one at a time, inside search and the tab, search older keeps the type, a reload starts at All');
  const chip = kind => page.locator(`#filters .chip[data-filter="${kind}"]`);
  const pressed = async () => page.locator('#filters .chip[aria-pressed="true"]').evaluateAll(els => els.map(e => e.dataset.filter));
  assert.deepEqual(await page.locator('#filters .chip').evaluateAll(els => els.map(e => e.textContent.trim())),
    ['All', 'Text', 'Links', 'Images', 'Files']);
  assert.equal(await page.locator('#filters .chip svg').count(), 4);
  assert.deepEqual(await pressed(), ['all']);
  await chip('file').click();
  assert.deepEqual(await pressed(), ['file']);
  assert.equal(await page.locator('.clip:visible').count(), 1);
  assert.equal(await fileRow.isVisible(), true);
  await page.fill('#search-input', 'notes');
  assert.equal(await page.locator('.clip:visible').count(), 1);
  await page.fill('#search-input', 'filler');
  assert.equal(await page.locator('.clip:visible').count(), 0);
  assert.equal(await text('#history-empty'), 'No clips match that search.');
  // The Files filter paged to the end of the history; reload so "Search older clips" has something left to fetch.
  await page.reload();
  await fileRow.waitFor();
  await page.fill('#search-input', 'needle');
  await chip('text').click();
  assert.equal(await page.locator('.clip:visible').count(), 0);
  await page.waitForSelector('#search-older:not([hidden])');
  await page.click('#search-older');
  await page.waitForSelector('#search-older', { state: 'hidden' });
  await row(needleId).waitFor();
  assert.equal(await page.locator('.clip:visible').count(), 1);
  await chip('link').click();
  assert.equal(await page.locator('.clip:visible').count(), 0);
  await page.fill('#search-input', '');
  await chip('link').click();
  assert.deepEqual(await pressed(), ['all']);
  for (const kind of ['text', 'link', 'image', 'file']) {
    await chip(kind).click();
    const kinds = await page.locator('.clip:visible').evaluateAll(els => [...new Set(els.map(e => e.dataset.kind))]);
    assert.deepEqual(kinds, [kind]);
  }
  await chip('all').click();
  assert.deepEqual(await pressed(), ['all']);
  assert.ok(await page.locator('.clip:visible').count() > 50);
  await chip('link').focus();
  await page.keyboard.press('Enter');
  assert.deepEqual(await pressed(), ['link']);
  await shoot('history-filter');
  await page.click('.tabs a:has-text("Pinned")');
  await page.waitForSelector('#clips-view:not([hidden])');
  assert.deepEqual(await pressed(), ['all']);
  await chip('file').click();
  await page.waitForSelector('#history-empty:not([hidden])');
  assert.equal(await text('#history-empty'), 'No pinned files');
  await page.goto(`${url}/`);
  await row(fileId).waitFor();
  assert.deepEqual(await pressed(), ['all']);

  step('a loaded clip under another key means encryption was reset in another tab: the key goes');
  await page.route('**/api/clips?*', async route => {
    const response = await route.fetch();
    const body = await response.json();
    body.items[0] = { ...body.items[0], keyId: '0123456789abcdef' };
    await route.fulfill({ response, json: body });
  });
  await page.reload();
  await page.waitForSelector('#unlock:not([hidden])', { timeout: 30000 });
  assert.equal(await text('#unlock-error'), 'Encryption was reset. Enter the new passphrase.');
  assert.equal(await page.evaluate(() => sessionStorage.getItem('pp.vault')), null);
  assert.equal(await visible('#lock'), false);
  assert.equal(await page.locator('.clip').count(), 0);
  await page.unroute('**/api/clips?*');

  step('an action answered 409 "Encryption was reset" does the same');
  await page.fill('#unlock-passphrase', passphrase);
  await page.click('#unlock-form button');
  await row(xssId).waitFor({ timeout: 30000 });
  await page.route('**/pin', route => route.fulfill({
    status: 409,
    contentType: 'application/problem+json',
    body: JSON.stringify({ status: 409, title: 'Encryption was reset', detail: 'Encryption was reset. Enter the new passphrase.' }),
  }));
  await row(linkId).locator('button.pin-toggle').click();
  await page.waitForSelector('#unlock:not([hidden])');
  assert.equal(await text('#unlock-error'), 'Encryption was reset. Enter the new passphrase.');
  assert.equal(await page.evaluate(() => sessionStorage.getItem('pp.vault')), null);
  assert.equal(await page.locator('.clip').count(), 0);
  await page.unroute('**/pin');

  step('a key from before a reset is dropped with a notice');
  await page.evaluate(() => sessionStorage.setItem('pp.vault', btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(32))))));
  await page.reload();
  await page.waitForSelector('#unlock:not([hidden])');
  assert.equal(await text('#unlock-error'), 'Encryption was reset. Enter the new passphrase.');
  assert.equal(await page.evaluate(() => sessionStorage.getItem('pp.vault')), null);
  assert.equal(await visible('#lock'), false);

  step('a wrap with settings outside the bounds is refused as unsupported');
  await page.route('**/api/vault', async route => {
    const response = await route.fetch();
    const wrap = await response.json();
    await route.fulfill({ response, json: { ...wrap, kdf: { ...wrap.kdf, iterations: 2 } } });
  });
  await page.fill('#unlock-passphrase', passphrase);
  await page.click('#unlock-form button');
  await until(() => document.getElementById('unlock-error').textContent.startsWith("This server's"));
  assert.equal(await text('#unlock-error'), "This server's encryption settings aren't supported. Update PrivatePaste.");
  await page.unroute('**/api/vault');

  step('a person who is not the owner is told to ask the owner, and a key without a vault is forgotten');
  await page.route('**/api/me', route => route.fulfill({ json: { name: 'ana', isOwner: false } }));
  await page.route('**/api/vault', route => route.fulfill({ status: 404, json: {} }));
  await page.evaluate(() => sessionStorage.setItem('pp.vault', btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(32))))));
  await page.reload();
  await page.waitForSelector('#no-vault:not([hidden])');
  assert.equal(await text('#no-vault'), 'Ask the owner to set up encryption.');
  assert.equal(await page.locator('#setup').isHidden(), true);
  assert.equal(await page.evaluate(() => sessionStorage.getItem('pp.vault')), null);
  assert.equal(await visible('#lock'), false);
  await page.unrouteAll();

  step('a failed who-am-I is a load error, not the non-owner panel');
  await page.route('**/api/me', route => route.fulfill({ status: 500, json: {} }));
  await page.reload();
  await page.waitForSelector('#history-error:not([hidden])');
  assert.equal(await text('#history-error'), "Couldn't load your history.");
  assert.equal(await page.locator('#no-vault').isHidden(), true);
  assert.equal(await page.locator('#setup').isHidden(), true);
  await page.unrouteAll();

  step('setup answered 409 (set up on another device meanwhile) asks for that passphrase');
  // The page sees no vault, so it offers setup; by the time it submits, the server has one (the real one).
  await page.route('**/api/vault', route => (route.request().method() === 'GET'
    ? route.fulfill({ status: 404, json: {} })
    : route.continue()));
  await page.reload();
  await page.waitForSelector('#setup:not([hidden])');
  await page.unrouteAll();
  await page.fill('#setup-passphrase', 'another long passphrase here');
  await page.fill('#setup-repeat', 'another long passphrase here');
  await page.click('#setup-form button');
  await page.waitForSelector('#unlock:not([hidden])', { timeout: 30000 });
  assert.equal(await text('#unlock-error'), 'Encryption was already set up on another device. Enter its passphrase.');
  assert.equal(await visible('#lock'), false);

  step('nothing broke the Content-Security-Policy');
  await page.reload();
  await page.fill('#unlock-passphrase', passphrase);
  await page.click('#unlock-form button');
  await until(id => document.querySelector(`.clip[data-id="${id}"] img.thumb`)?.naturalWidth > 0, imageId);

  step('a type whose matches sit beyond the first page loads older pages by itself, never a blank list with Load more');
  const newer = await upload(Array.from({ length: 50 }, (_, i) => ({ kind: 'text', text: `newer clip ${i}` })));
  await page.reload();
  await row(newer[49]).waitFor({ timeout: 30000 });
  assert.equal(await page.locator('.clip[data-kind="image"]').count(), 0);
  await page.locator('#filters .chip[data-filter="image"]').click();
  await until(() => document.querySelectorAll('.clip[data-kind="image"]:not([hidden])').length === 2);
  assert.equal(await page.locator('.clip:visible').count(), 2);
  assert.equal(await visible('#history-empty'), false);
  assert.equal(await visible('#load-more'), false);
  await shoot('history-filter-older');
  await page.locator('#filters .chip[data-filter="all"]').click();

  // ---- Settings: the encryption panels ----
  const unlockWith = async phrase => {
    await page.fill('#unlock-passphrase', phrase);
    await page.click('#unlock-form button');
  };
  const lockAndReload = async () => {
    await page.goto(`${url}/`);
    await page.waitForSelector('#lock:not([hidden])');
    await page.click('#lock');
    await page.waitForSelector('#unlock:not([hidden])');
  };

  step('Settings: Add a device is available while unlocked, and the owner sees the other two panels');
  await page.goto(`${url}/settings`);
  await page.waitForSelector('#add-device-open:not([disabled])');
  assert.equal(await visible('#cp-form'), true);
  assert.equal(await visible('#reset-open'), true);
  assert.equal(await visible('#encryption-note'), false);
  await shoot('settings');

  step('Change passphrase: checks, then the old passphrase fails and the new one unlocks');
  await page.fill('#cp-current', passphrase);
  await page.fill('#cp-new', 'too short');
  await page.fill('#cp-repeat', 'too short');
  await page.click('#cp-form button');
  await until(() => document.getElementById('cp-error').textContent.startsWith('Use at least 12'));
  await page.fill('#cp-new', changedPassphrase);
  await page.fill('#cp-repeat', changedPassphrase + '!');
  await page.click('#cp-form button');
  await until(() => document.getElementById('cp-error').textContent === "The passphrases don't match.");
  await page.fill('#cp-current', 'not the current passphrase');
  await page.fill('#cp-repeat', changedPassphrase);
  await page.click('#cp-form button');
  await until(() => document.getElementById('cp-error').textContent === 'The current passphrase is wrong.');
  await page.fill('#cp-current', passphrase);
  await page.click('#cp-form button');
  await until(() => document.getElementById('cp-status').textContent === 'Passphrase changed.');
  assert.equal(await visible('#cp-error'), false);
  assert.equal(await page.inputValue('#cp-new'), '');
  await lockAndReload();
  await unlockWith(passphrase);
  await page.waitForSelector('#unlock-error:not([hidden])', { timeout: 30000 });
  assert.equal(await text('#unlock-error'), "That passphrase doesn't unlock this server's history.");
  await unlockWith(changedPassphrase);
  await page.waitForSelector('#clips-view:not([hidden])', { timeout: 30000 });

  step('Add a device: the code carries a handover that opens to the same key, once');
  await page.goto(`${url}/settings`);
  await page.waitForSelector('#add-device-open:not([disabled])');
  await page.click('#add-device-open');
  await page.waitForSelector('#qr-code[data-qr]');
  assert.equal(await visible('#add-device-code'), true);
  assert.match(await text('#add-device-code'), /Scan this with PrivatePaste on your phone\. It works once, for 10 minutes\./);
  assert.match(await text('#add-device-time'), /^(9:5\d|10:00) left$/);
  assert.ok(await page.evaluate(() => document.getElementById('qr-code').width > 100));
  await shoot('add-device');
  const opened = await page.evaluate(async () => {
    const e2e = await import(document.getElementById('add-device').dataset.e2e);
    const headers = { 'X-PrivatePaste-Protocol': '2' };
    const parsed = e2e.parseHandoverQr(document.getElementById('qr-code').getAttribute('data-qr'));
    const wrap = await (await fetch('/api/vault', { headers })).json();
    const first = await fetch(`/api/vault/handovers/${parsed.id}`, { headers });
    const blob = Uint8Array.from(atob((await first.json()).blob), c => c.charCodeAt(0));
    const vault = await e2e.openHandover(parsed.otk, blob, parsed.keyId);
    const second = await fetch(`/api/vault/handovers/${parsed.id}`, { headers });
    return { parsed: parsed.keyId, opened: vault.keyId, server: wrap.keyId, secondStatus: second.status };
  });
  assert.equal(opened.parsed, opened.server);
  assert.equal(opened.opened, opened.server);
  assert.equal(opened.secondStatus, 404);

  step('closing the dialog drops the code');
  await page.click('#add-device-close');
  assert.equal(await page.locator('#qr-code').getAttribute('data-qr'), null);
  assert.equal(await visible('#add-device-code'), false);

  step('a code that cannot be made says so and offers a new one');
  await page.route('**/api/vault/handovers', route => route.fulfill({ status: 500, json: {} }));
  await page.click('#add-device-open');
  await page.waitForSelector('#add-device-error:not([hidden])');
  assert.equal(await text('#add-device-error'), "Couldn't make a code. Check your connection.");
  assert.equal(await page.locator('#qr-code').getAttribute('data-qr'), null);
  await page.unroute('**/api/vault/handovers');
  await page.click('#add-device-new');
  await page.waitForSelector('#qr-code[data-qr]');
  assert.equal(await visible('#add-device-error'), false);

  step('the code is hidden when the countdown reaches 0');
  await page.click('#add-device-close');
  await page.clock.install();
  await page.click('#add-device-open');
  await page.waitForSelector('#qr-code[data-qr]');
  await page.clock.fastForward('09:58');
  await until(() => /^0:0[0-3] left$/.test(document.getElementById('add-device-time').textContent));
  assert.notEqual(await page.locator('#qr-code').getAttribute('data-qr'), null);
  await page.clock.fastForward('00:05');
  await page.waitForSelector('#add-device-code', { state: 'hidden' });
  assert.equal(await page.locator('#qr-code').getAttribute('data-qr'), null);
  assert.equal(await text('#add-device-new'), 'Show a new code');
  await page.click('#add-device-new');
  await page.waitForSelector('#qr-code[data-qr]');
  assert.match(await text('#add-device-time'), /^(9:5\d|10:00) left$/);
  await page.click('#add-device-close');

  step('Reset encryption: the dialog states the cost, checks the passphrase, and starts over');
  const keyBefore = await page.evaluate(() => sessionStorage.getItem('pp.vault'));
  await page.click('#reset-open');
  await page.waitForSelector('#reset-dialog[open]');
  assert.match(await text('#reset-dialog'),
    /This deletes the whole history, pinned clips included, on every device\. Devices need the new passphrase\./);
  assert.equal(await text('#reset-dialog button.danger'), 'Delete history and reset');
  await shoot('reset-dialog');
  await page.fill('#reset-new', 'short one');
  await page.fill('#reset-repeat', 'short one');
  await page.click('#reset-form button.danger');
  await until(() => document.getElementById('reset-error').textContent.startsWith('Use at least 12'));
  await page.fill('#reset-new', resetPassphrase);
  await page.fill('#reset-repeat', resetPassphrase + '!');
  await page.click('#reset-form button.danger');
  await until(() => document.getElementById('reset-error').textContent === "The passphrases don't match.");
  await page.click('#reset-cancel');
  assert.equal(await page.locator('#reset-dialog[open]').count(), 0);
  await page.click('#reset-open');
  assert.equal(await page.inputValue('#reset-new'), '');
  await page.fill('#reset-new', resetPassphrase);
  await page.fill('#reset-repeat', resetPassphrase);
  await page.click('#reset-form button.danger');
  await until(() => !document.getElementById('reset-dialog').open, undefined, 30000);
  const keyAfter = await page.evaluate(() => sessionStorage.getItem('pp.vault'));
  assert.ok(keyAfter && keyAfter !== keyBefore, 'this tab holds the new key');
  await page.goto(`${url}/`);
  await page.waitForSelector('#history-empty:not([hidden])', { timeout: 30000 });
  assert.equal(await page.locator('.clip').count(), 0);
  await page.click('#lock');
  await page.waitForSelector('#unlock:not([hidden])');
  await unlockWith(changedPassphrase);
  await page.waitForSelector('#unlock-error:not([hidden])', { timeout: 30000 });
  assert.equal(await text('#unlock-error'), "That passphrase doesn't unlock this server's history.");
  await unlockWith(resetPassphrase);
  await page.waitForSelector('#history-empty:not([hidden])', { timeout: 30000 });

  assert.deepEqual(cspViolations, []);
  assert.deepEqual(pageErrors, []);

  console.log('history.playwright.mjs: all checks passed');
} catch (error) {
  // Closing the browser below would otherwise bury this under a TargetClosedError.
  console.error(error);
  process.exitCode = 1;
} finally {
  await browser.close();
}
