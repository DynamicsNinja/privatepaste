// End-to-end check of the extension in real Edge against a real server, run by hand (not in CI, and not by
// `node --test`):
//
//   node extension/test/e2e.playwright.mjs
//
// It starts its own LOCAL server (PP_PASSWORD=dev, a throwaway PP_DATA_DIR, a free localhost port), builds the
// extension with the FAKE clipboard (the OS clipboard is never written) and loads it into a headed Edge with a throwaway
// profile. A second device ("Phone") is simulated through the API with its own token; it also sets up encryption. Two
// sign-ins in all (the phone, the extension through its popup), under the server's 5 a minute. It checks:
//   1. a text set on the fake clipboard shows up in the phone's GET /api/clips within 3 s, over the socket;
//   2. a text the phone uploads lands on the fake clipboard within 3 s, and is not uploaded back;
//   3. Edge closed and reopened on the same profile: Syncing again without the passphrase, and a clip the phone uploaded
//      meanwhile is applied exactly once;
//   4. the server stopped, a text copied, the server started again on the same data dir and port: the copy arrives as a
//      backfill (see that step for how that is told apart from a live upload).
// Prints no token, key or clip content. Uses Playwright from scripts/readme (run `npm ci` there once).

import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import * as e2e from '../../src/PrivatePaste.Server/wwwroot/js/e2e.js';
import { apiClient, buildFakeExtension, extensionWorker, launchEdge, startServer, waitFor } from './helpers/local.mjs';

const passphrase = 'purple walrus eats pancakes at dawn';
const work = mkdtempSync(join(tmpdir(), 'pp-ext-e2e-'));
const dataDir = join(work, 'data');
const profile = join(work, 'profile');
const out = join(work, 'ext');
const step = name => console.log(`- ${name}`);

let server = await startServer({ dataDir });
const { url } = server;
const port = Number(new URL(url).port);
const { call, login } = apiClient(url);

// ---- the phone: a device of its own, and the encryption ----
const phone = await login('Phone', 'android');
const vault = await e2e.importVaultKey(crypto.getRandomValues(new Uint8Array(32)));
await call('PUT', '/api/vault', { token: phone.token, json: await e2e.wrapWithPassphrase(vault, passphrase) });

/** The phone copies `text`: a live upload, as the apps send it. */
async function phoneCopies(text) {
  const meta = {
    kind: 'text', keyId: vault.keyId, hash: await e2e.keyedHash(vault, await e2e.contentHashText(text)),
    payload: await e2e.sealPayload(vault, { text }), sizeBytes: new TextEncoder().encode(text).length, backfill: false,
  };
  const form = new FormData();
  form.append('meta', new Blob([JSON.stringify(meta)], { type: 'application/json' }), 'meta.json');
  return call('POST', '/api/clips', { token: phone.token, body: form });
}

/** The phone's view of the history: every clip on the first page with its decrypted text. */
async function phoneHistory() {
  const { items } = await call('GET', '/api/clips?limit=200', { token: phone.token });
  return Promise.all(items.map(async clip => ({ ...clip, plain: (await e2e.openPayload(vault, clip.payload)).text })));
}
const withText = async text => (await phoneHistory()).filter(c => c.plain === text);
/** The clip holding `text`, once there is one. */
const arrived = async text => (await withText(text))[0] ?? null;

buildFakeExtension(out);

let ctx;
let failed = false;
try {
  ctx = await launchEdge(profile, out);
  let sw = await extensionWorker(ctx);
  const send = message => sw.evaluate(m => chrome.runtime.sendMessage(m), message);
  // The offscreen document may not be listening yet right after a start.
  const ask = async message => { try { return await send(message); } catch { return null; } };
  const status = async () => (await ask({ type: 'engine.status' }))?.value ?? null;
  const clipboard = () => ask({ type: 'test.clipboard.get' });
  const copyHere = text => send({ type: 'test.clipboard.set', text });
  const queued = () => sw.evaluate(() => new Promise((resolve, reject) => {
    const r = indexedDB.open('privatepaste', 1);
    r.onerror = () => reject(r.error);
    r.onsuccess = () => {
      const keys = r.result.transaction('kv').objectStore('kv').getAllKeys();
      keys.onsuccess = () => { resolve(keys.result.filter(k => String(k).startsWith('queue:')).length); r.result.close(); };
      keys.onerror = () => reject(keys.error);
    };
  }));
  const syncingOverSocket = async () => {
    const s = await status();
    return s?.state === 'syncing' && s.link === 'socket';
  };

  step('sign in and unlock in the popup');
  const extensionId = new URL(sw.url()).host;
  const popup = await ctx.newPage();
  await popup.goto(`chrome-extension://${extensionId}/popup.html`);
  await popup.waitForSelector('#view-signin:not([hidden])', { timeout: 15000 });
  await popup.fill('#server', url);
  await popup.fill('#username', 'owner');
  await popup.fill('#password', 'dev');
  await popup.click('#signin-form button[type=submit]');
  await popup.waitForSelector('#view-unlock:not([hidden])', { timeout: 20000 });
  await popup.fill('#passphrase', passphrase);
  await popup.click('#unlock-form button[type=submit]');
  await popup.waitForSelector('#view-history:not([hidden])', { timeout: 20000 });
  await popup.close();
  await waitFor('Syncing over the socket', syncingOverSocket);
  const extensionDevice = (await status()).deviceName;
  assert.equal(extensionDevice, 'Edge');

  step('a copy in the browser reaches the other device within 3 s');
  const local = `copied in Edge ${Date.now()}`;
  let at = Date.now();
  await copyHere(local);
  const uploaded = await waitFor('the copy on the server', () => arrived(local), 10000);
  const upMs = Date.now() - at;
  assert.ok(upMs <= 3000, `arrived after ${upMs} ms`);
  assert.equal(uploaded.sourceDevicePlatform, 'extension');
  assert.notEqual(uploaded.sourceDeviceId, phone.deviceId);
  console.log(`  ${upMs} ms`);

  step('a copy on the other device lands on the clipboard within 3 s, once, and is not uploaded back');
  const remote = `copied on the phone ${Date.now()}`;
  const writesBefore = (await clipboard()).writes;
  at = Date.now();
  const sent = await phoneCopies(remote);
  await waitFor('the phone\'s copy on the fake clipboard', async () => (await clipboard())?.text === remote, 10000);
  const downMs = Date.now() - at;
  assert.ok(downMs <= 3000, `applied after ${downMs} ms`);
  console.log(`  ${downMs} ms`);
  // The poller reads the clipboard every second: three reads of the written text, none of which may upload it.
  await new Promise(r => setTimeout(r, 3500));
  assert.equal((await clipboard()).writes, writesBefore + 1, 'written exactly once');
  const remoteNow = await withText(remote);
  assert.equal(remoteNow.length, 1);
  // An upload of the same content would bump this clip: new seq, the extension as its source, the server's time.
  assert.equal(remoteNow[0].seq, sent.seq);
  assert.equal(remoteNow[0].sourceDeviceId, phone.deviceId);
  assert.equal(remoteNow[0].lastCopiedAt, sent.lastCopiedAt);
  assert.equal((await phoneHistory())[0].id, sent.id, 'nothing newer was uploaded');

  step('Edge closed and reopened: syncing without the passphrase, the clip from meanwhile applied once');
  await ctx.close();
  const whileClosed = `copied while Edge was closed ${Date.now()}`;
  const sentClosed = await phoneCopies(whileClosed);
  ctx = await launchEdge(profile, out);
  sw = await extensionWorker(ctx);
  const seen = [];
  await waitFor('Syncing over the socket after the restart', async () => {
    const s = await status();
    if (s && seen.at(-1) !== s.state) seen.push(s.state);
    return s?.state === 'syncing' && s.link === 'socket';
  });
  assert.ok(!seen.includes('locked') && !seen.includes('signedOut'), `states seen: ${seen.join(', ')}`);
  await waitFor('the clip from meanwhile on the fake clipboard', async () => (await clipboard())?.text === whileClosed, 10000);
  await new Promise(r => setTimeout(r, 3500));
  // A new offscreen document, so its count starts at 0: one write is the catch-up's, and nothing re-applied it.
  assert.equal((await clipboard()).writes, 1, 'applied exactly once');
  const closedNow = await withText(whileClosed);
  assert.equal(closedNow.length, 1);
  assert.equal(closedNow[0].seq, sentClosed.seq, 'not uploaded back');

  step('offline: a copy made while the server is down arrives as a backfill once it is back');
  await server.stop();
  await waitFor('Offline', async () => (await status())?.state === 'offline');
  const offline = `copied while the server was down ${Date.now()}`;
  const copiedAt = Date.now();
  await copyHere(offline);
  await waitFor('the copy in the queue', async () => (await queued()) === 1, 10000);
  const restartedAt = Date.now();
  server = await startServer({ dataDir, port });
  const backfilled = await waitFor('the queued copy on the server', () => arrived(offline), 90000);
  // The backfill flag isn't in the clip; its time tells. A live upload gets the server's time, which is after the
  // restart began; a backfill keeps the copy time, taken when the poller read the clipboard (within about a second of
  // the set), and that was before the restart. The queue being empty again says the engine's flush (the only path that
  // sends backfill: true) sent it.
  const lastCopied = Date.parse(backfilled.lastCopiedAt);
  assert.ok(lastCopied >= copiedAt - 1000 && lastCopied < restartedAt,
    `lastCopiedAt ${backfilled.lastCopiedAt}, copied ${new Date(copiedAt).toISOString()}, restart ${new Date(restartedAt).toISOString()}`);
  assert.equal(backfilled.sourceDevicePlatform, 'extension');
  await waitFor('an empty queue', async () => (await queued()) === 0, 10000);
  await waitFor('Syncing over the socket again', syncingOverSocket, 60000);
  console.log('PASS');
} catch (e) {
  failed = true;
  console.error(e);
} finally {
  process.on('unhandledRejection', e => { if (e?.name !== 'TargetClosedError') throw e; });
  await ctx?.close().catch(() => {});
  await server.stop().catch(() => {});
  rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
}
process.exitCode = failed ? 1 : 0;
