// Real-Edge check of the toolbar popup, run by hand (not in CI, and not by `node --test`):
//
//   node extension/test/popup.playwright.mjs [--url http://localhost:5304]
//
// Without --url it starts its own local server (PP_PASSWORD=dev, a throwaway PP_DATA_DIR, a free port) and stops it at
// the end; with --url the server must be FRESH (no vault yet). Never point it at the live server. It builds the extension
// with the FAKE clipboard, loads it into a headed Edge with a throwaway profile, sets up encryption through the web UI,
// uploads clips from the web (another device), then opens the toolbar popup (chrome.action.openPopup), which hands
// sign-in over to a tab, and drives popup.html in that tab: failed sign-ins and their messages, sign in, unlock, rows,
// chips, search, tabs, Copy (text through the engine, an image through navigator.clipboard.write, stubbed here so the
// real clipboard is never written), Download, Pause syncing, the settings note and Sign out. With PP_SCREENSHOT_DIR set
// it saves the views in dark and light. Three sign-ins (the web, a wrong password, the extension): under the server's
// 5 a minute.
//
// The built manifest gets host_permissions for http://localhost/*, so the Sign in click's permissions.request finds the
// origin granted and shows no prompt (Playwright can't click a browser prompt). That prompt is why sign-in runs in a
// tab: Edge closes the toolbar popup when it opens.

import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { buildFakeExtension, freePort, launchEdge, startServer } from './helpers/local.mjs';

const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const passphrase = 'purple walrus eats pancakes at dawn';
const shots = process.env.PP_SCREENSHOT_DIR;
if (shots) mkdirSync(shots, { recursive: true });

const work = mkdtempSync(join(tmpdir(), 'pp-ext-popup-'));
const out = join(work, 'ext');
const profile = join(work, 'profile');
const step = name => console.log(`- ${name}`);

let server;
const url = arg('url', null) ?? (server = await startServer({ dataDir: join(work, 'data') })).url;
if (/ficdev\.xyz/i.test(url)) throw new Error('Never run this against the live server.');

buildFakeExtension(out);

const ctx = await launchEdge(profile, out, { acceptDownloads: true });

let failed = false;
try {
  let [sw] = ctx.serviceWorkers();
  sw ??= await ctx.waitForEvent('serviceworker');
  const extensionId = new URL(sw.url()).host;
  const send = message => sw.evaluate(m => chrome.runtime.sendMessage(m), message);

  // ---- the web UI: encryption set up, clips from "another device" ----
  step('set up encryption on the web');
  const web = await ctx.newPage();
  await web.goto(`${url}/login`);
  await web.fill('#Name', 'owner');
  await web.fill('#Password', 'dev');
  await Promise.all([web.waitForURL(`${url}/`), web.click('button[type=submit]')]);
  await web.waitForSelector('#setup:not([hidden])', { timeout: 15000 }).catch(() => {
    throw new Error('No setup panel: run this against a fresh server (an empty PP_DATA_DIR).');
  });
  await web.fill('#setup-passphrase', passphrase);
  await web.fill('#setup-repeat', passphrase);
  await web.click('#setup-form button');
  await web.waitForSelector('#clips-view:not([hidden])', { timeout: 30000 });

  // Uploads clips sealed with the web tab's key (the same helper as tests/web/history.playwright.mjs).
  const upload = clips => web.evaluate(async clips => {
    const shell = document.getElementById('history');
    const e2e = await import(shell.dataset.e2e);
    const { api } = await import(shell.dataset.history);
    const raw = Uint8Array.from(atob(sessionStorage.getItem('pp.vault')), c => c.charCodeAt(0));
    const vault = await e2e.importVaultKey(raw);
    const ids = [];
    for (const clip of clips) {
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
      if (!response.ok) throw new Error(`upload ${clip.kind}: HTTP ${response.status}`);
      ids.push((await response.json()).id);
    }
    return ids;
  }, clips);
  const pin = id => web.evaluate(async id => {
    const { api } = await import(document.getElementById('history').dataset.history);
    const response = await api(`/api/clips/${encodeURIComponent(id)}/pin`, { method: 'POST' });
    if (!response.ok) throw new Error(`pin: HTTP ${response.status}`);
  }, id);

  const [imageId, notesId, pairId, meetingId, linkId] = await upload([
    { kind: 'image', width: 320, height: 180 },
    { kind: 'file', files: [{ name: 'notes.txt', content: 'hello from the phone\n' }] },
    { kind: 'file', files: [{ name: 'a.txt', content: 'first file' }, { name: 'b.txt', content: 'second file, longer' }] },
    { kind: 'text', text: 'Meeting moved to 3:30, room 4B' },
    { kind: 'link', text: 'https://github.com/DynamicsNinja/privatepaste' },
  ]);

  // ---- the popup ----
  // Never write the real clipboard: the image Copy goes through navigator.clipboard.write, recorded here instead. On the
  // context, so it also runs in the sign-in tab the toolbar popup opens.
  await ctx.addInitScript(() => {
    if (location.protocol !== 'chrome-extension:') return;
    window.__writes = [];
    Object.defineProperty(navigator.clipboard, 'write', {
      configurable: true,
      value: async items => {
        for (const item of items) {
          const types = [...item.types];
          const blob = await item.getType(types[0]);
          const bytes = new Uint8Array(await blob.arrayBuffer());
          window.__writes.push({ types, size: bytes.length, png: bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47 });
        }
      },
    });
    // Counts what the popup asks the engine, so the idle refresh can be checked.
    window.__asked = [];
    const send = chrome.runtime.sendMessage.bind(chrome.runtime);
    chrome.runtime.sendMessage = (message, ...rest) => { window.__asked.push(message?.type); return send(message, ...rest); };
    Object.defineProperty(navigator.clipboard, 'writeText', { configurable: true, value: async () => { window.__writes.push({ types: ['text'] }); } });
  });

  // Edge closes the toolbar popup when the host permission prompt opens, so signing in there ended with no message.
  step('signed out, the toolbar popup opens the sign-in form in a tab');
  await web.bringToFront();
  const opened = ctx.waitForEvent('page', { timeout: 15000 });
  assert.equal(await sw.evaluate(() => chrome.action.openPopup().then(() => 'opened', e => e.message)), 'opened');
  const popup = await opened;
  await popup.waitForURL(/\/popup\.html$/);
  await popup.setViewportSize({ width: 380, height: 600 });
  // page.waitForFunction evaluates a string in the page, which the extension's CSP blocks; this polls with evaluate.
  const until = async (fn, arg, timeout = 20000) => {
    const end = Date.now() + timeout;
    while (!(await popup.evaluate(fn, arg))) {
      if (Date.now() > end) throw new Error(`Timed out waiting for ${fn}`);
      await popup.waitForTimeout(100);
    }
  };
  const pageErrors = [];
  popup.on('pageerror', e => pageErrors.push(e.message));
  await popup.waitForSelector('#view-signin:not([hidden])', { timeout: 15000 });
  assert.equal(await popup.inputValue('#device-name'), 'Edge');
  assert.equal(await popup.evaluate(() => document.body.classList.contains('in-tab')), true);

  step('a failed sign-in says why, and the form stays');
  const failedSignIn = async (server, username, password, message) => {
    await popup.fill('#server', server);
    await popup.fill('#username', username);
    await popup.fill('#password', password);
    await popup.click('#signin-form button[type=submit]');
    await until(text => {
      const error = document.getElementById('signin-error');
      return !error.hidden && error.textContent === text && !document.getElementById('signin-form').hasAttribute('aria-busy');
    }, message, 20000);
    assert.equal(await popup.isVisible('#view-signin'), true);
  };
  await failedSignIn(url, 'owner', 'not the password', 'Wrong name or password.');
  await failedSignIn(`http://localhost:${await freePort()}`, 'owner', 'dev', "Can't reach the server.");
  await failedSignIn('http://paste.example.com', 'owner', 'dev', 'The server address must use https:// (http:// is only allowed for localhost).');
  if (shots) {
    for (const scheme of ['dark', 'light']) {
      await popup.emulateMedia({ colorScheme: scheme });
      await popup.waitForTimeout(400);
      await popup.screenshot({ path: join(shots, `ext-signin-failed-${scheme}.png`) });
    }
    await popup.emulateMedia({ colorScheme: 'dark' });
  }

  step('sign in');
  await popup.fill('#server', url);
  await popup.fill('#username', 'owner');
  await popup.fill('#password', 'dev');
  await popup.click('#signin-form button[type=submit]');
  await popup.waitForSelector('#view-unlock:not([hidden])', { timeout: 20000 });
  assert.equal(await popup.innerText('#status-text'), 'Locked');

  step('a wrong passphrase says so');
  await popup.fill('#passphrase', 'not the passphrase at all');
  await popup.click('#unlock-form button[type=submit]');
  await popup.waitForSelector('#unlock-error:not([hidden])', { timeout: 20000 });
  assert.equal(await popup.innerText('#unlock-error'), "That passphrase doesn't unlock this server's history.");

  step('unlock');
  await popup.fill('#passphrase', passphrase);
  await popup.click('#unlock-form button[type=submit]');
  await popup.waitForSelector('#view-history:not([hidden])', { timeout: 20000 });
  const row = id => popup.locator(`.clip[data-id="${id}"]`);
  await row(linkId).waitFor({ timeout: 20000 });
  await until(() => document.getElementById('status-text').textContent === 'Syncing', undefined, 20000);

  step('a text clip from another device arrives as a row with the text icon');
  const [liveId] = await upload([{ kind: 'text', text: 'docker compose pull && docker compose up -d' }]);
  await row(liveId).waitFor({ timeout: 20000 });
  assert.equal(await row(liveId).getAttribute('data-kind'), 'text');
  assert.equal(await row(liveId).locator('.kind svg[data-kind="text"]').count(), 1);
  assert.equal(await row(liveId).locator('.title').innerText(), 'docker compose pull && docker compose up -d');
  assert.match(await row(liveId).locator('.meta').innerText(), /^.+ · just now$/);
  assert.equal(await row(liveId).locator('.meta svg[data-platform="web"]').count(), 1);
  assert.equal(await popup.locator('.clip').first().getAttribute('data-id'), liveId);
  assert.equal(await row(imageId).locator('.kind img.thumb').count(), 1, 'image row shows its thumbnail');
  await until(id => document.querySelector(`.clip[data-id="${id}"] img.thumb`)?.naturalWidth > 0, imageId, 20000);
  assert.equal(await row(imageId).locator('.title').innerText(), 'Image · 320 × 180');
  assert.equal(await row(notesId).locator('.title').innerText(), 'notes.txt · 21 B');
  await popup.waitForTimeout(500);
  if (shots) {
    for (const scheme of ['dark', 'light']) {
      await popup.emulateMedia({ colorScheme: scheme });
      await popup.waitForTimeout(400);
      await popup.screenshot({ path: join(shots, `ext-popup-history-${scheme}.png`) });
    }
    await popup.emulateMedia({ colorScheme: 'dark' });
  }

  step('while nothing changes, the refresh asks for the status and the history version only');
  await popup.evaluate(() => { window.__asked.length = 0; });
  await popup.waitForTimeout(5000);
  const idle = await popup.evaluate(() => window.__asked);
  assert.ok(idle.includes('engine.historyVersion'), 'the refresh ran');
  assert.deepEqual([...new Set(idle)].sort(), ['engine.historyVersion', 'engine.status']);

  const visibleIds = () => popup.locator('.clip:not([hidden])').evaluateAll(rows => rows.map(r => r.dataset.id));

  step('the Links chip shows only link rows');
  await popup.click('.chip[data-filter="link"]');
  assert.deepEqual(await visibleIds(), [linkId]);
  assert.equal(await popup.getAttribute('.chip[data-filter="link"]', 'aria-pressed'), 'true');
  await popup.click('.chip[data-filter="link"]');
  assert.equal((await visibleIds()).length, 6);

  step('search filters');
  await popup.fill('#search', 'MEETING');
  assert.deepEqual(await visibleIds(), [meetingId]);
  await popup.fill('#search', 'b.txt');
  assert.deepEqual(await visibleIds(), [pairId]);
  await popup.fill('#search', 'nothing matches this');
  assert.deepEqual(await visibleIds(), []);
  assert.equal(await popup.innerText('#empty'), 'No clips match that search.');
  // Six clips: the engine's cache holds the whole history, so there is nothing older to search.
  assert.equal(await popup.isVisible('#search-older'), false);
  await popup.fill('#search', '');
  assert.equal((await visibleIds()).length, 6);

  step('History and Pinned tabs switch');
  await pin(meetingId);
  await popup.click('.tab[data-tab="pinned"]');
  assert.equal(await popup.getAttribute('.tab[data-tab="pinned"]', 'aria-selected'), 'true');
  assert.equal(await popup.getAttribute('#search', 'placeholder'), 'Search pinned clips');
  await until(id => {
    const ids = [...document.querySelectorAll('.clip:not([hidden])')].map(r => r.dataset.id);
    return ids.length === 1 && ids[0] === id;
  }, meetingId, 20000);
  await popup.click('.tab[data-tab="history"]');
  await row(liveId).waitFor({ timeout: 10000 });
  assert.equal((await visibleIds()).length, 6);

  step('Copy on a text row puts it on the (fake) clipboard');
  await row(meetingId).locator('button.act').click();
  await until(id => document.querySelector(`.clip[data-id="${id}"] button.act`)?.classList.contains('done'), meetingId, 10000);
  assert.equal((await send({ type: 'test.clipboard.get' })).text, 'Meeting moved to 3:30, room 4B');

  step('Copy on an image row writes a PNG through navigator.clipboard.write');
  await row(imageId).locator('button.act').click();
  await until(id => document.querySelector(`.clip[data-id="${id}"] button.act`)?.classList.contains('done'), imageId, 20000);
  const writes = await popup.evaluate(() => window.__writes);
  assert.equal(writes.length, 1);
  assert.deepEqual(writes[0].types, ['image/png']);
  assert.ok(writes[0].png && writes[0].size > 100, 'a PNG was written');

  step('a file row downloads the right bytes');
  const download = async locator => {
    const [d] = await Promise.all([popup.waitForEvent('download', { timeout: 20000 }), locator.click()]);
    return { name: d.suggestedFilename(), text: readFileSync(await d.path(), 'utf8') };
  };
  assert.deepEqual(await download(row(notesId).locator('button.act')), { name: 'notes.txt', text: 'hello from the phone\n' });
  await row(pairId).locator('button.act').click();
  await row(pairId).locator('.file-line').nth(1).waitFor();
  assert.deepEqual(await download(row(pairId).locator('.file-line').nth(1).locator('button')), { name: 'b.txt', text: 'second file, longer' });

  step('settings: Pause syncing shows Paused, and the password-manager note');
  await popup.click('#open-settings');
  await popup.waitForSelector('#view-settings:not([hidden])');
  assert.match(await popup.innerText('#view-settings'), /Copies from password managers sync too: the extension can't tell them apart\./);
  assert.equal(await popup.innerText('#settings-server'), url);
  assert.equal(await popup.innerText('#settings-device'), 'Edge');
  await popup.click('#pause');
  await until(() => document.getElementById('status-text').textContent === 'Paused', undefined, 10000);
  assert.equal(await popup.isChecked('#pause'), true);
  if (shots) {
    for (const scheme of ['dark', 'light']) {
      await popup.emulateMedia({ colorScheme: scheme });
      await popup.waitForTimeout(400);
      await popup.screenshot({ path: join(shots, `ext-popup-settings-${scheme}.png`) });
    }
    await popup.emulateMedia({ colorScheme: 'dark' });
  }
  await popup.click('#pause');
  await until(() => document.getElementById('status-text').textContent !== 'Paused', undefined, 10000);

  step('Sign out returns to sign-in');
  await popup.click('#sign-out');
  await popup.waitForSelector('#view-signin:not([hidden])', { timeout: 15000 });
  assert.equal(await popup.isVisible('#status'), false);
  if (shots) {
    for (const scheme of ['dark', 'light']) {
      await popup.emulateMedia({ colorScheme: scheme });
      await popup.waitForTimeout(400);
      await popup.screenshot({ path: join(shots, `ext-popup-signin-${scheme}.png`) });
    }
  }
  step('signed out, the toolbar popup brings the open sign-in tab forward instead of opening another');
  const tabs = () => ctx.pages().filter(p => p.url().endsWith('/popup.html')).length;
  await web.bringToFront();
  assert.equal(await sw.evaluate(() => chrome.action.openPopup().then(() => 'opened', e => e.message)), 'opened');
  await until(() => document.visibilityState === 'visible', undefined, 10000);
  assert.equal(tabs(), 1);

  assert.deepEqual(pageErrors, []);
  console.log('PASS');
} catch (e) {
  failed = true;
  console.error(e);
} finally {
  // Closing Edge rejects Playwright's wait for a tab Edge opened on its own (TargetClosedError); that's not a failure.
  process.on('unhandledRejection', e => { if (e?.name !== 'TargetClosedError') throw e; });
  await ctx.close().catch(() => {});
  await server?.stop().catch(() => {});
  rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
}
process.exitCode = failed ? 1 : 0;
