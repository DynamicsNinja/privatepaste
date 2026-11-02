// Real-Edge check that the sync socket authenticates with the subprotocols `privatepaste.v2` and
// `privatepaste.auth.<token>` (ruling R-X8), run by hand (not in CI, and not by `node --test`):
//
//   node extension/test/socket.playwright.mjs
//
// It starts its own LOCAL server (PP_PASSWORD=dev, a throwaway PP_DATA_DIR, a free localhost port), sets up encryption
// (owner/dev), signs in three devices through the API (under the server's 5 sign-ins per minute), builds the extension
// with the FAKE clipboard (the OS clipboard is never written) plus a test-only host permission for localhost, and loads
// it into Edge (headed, throwaway profile). It hands the extension one device's token and vault through its IndexedDB,
// restarts the offscreen document, and checks:
//   1. the engine reaches Syncing over the socket (status link 'socket'), which only happens once the server accepted
//      the upgrade, i.e. took the offered token and selected privatepaste.v2;
//   2. a text clip uploaded live by another device lands on the fake clipboard (over the socket, or by polling).
// PP_WS_DEBUG=1 shows the server's request log (the request line only, never the subprotocol header).
// Prints no token, key or clip content. Uses Playwright from scripts/readme (run `npm ci` there once).

import { execFileSync, spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as e2e from '../../src/PrivatePaste.Server/wwwroot/js/e2e.js';

const require = createRequire(new URL('../../scripts/readme/package.json', import.meta.url));
const { chromium } = require('playwright');

const repo = fileURLToPath(new URL('../..', import.meta.url));
const work = mkdtempSync(join(tmpdir(), 'pp-ext-ws-'));
const port = await new Promise(resolve => {
  const probe = createServer().listen(0, 'localhost', () => { const p = probe.address().port; probe.close(() => resolve(p)); });
});
const server = `http://localhost:${port}`;
const host = spawn('dotnet', ['run', '--project', join(repo, 'src', 'PrivatePaste.Server'), '--no-launch-profile', '--urls', server], {
  env: {
    ...process.env, PP_DB_KEY: '0123456789abcdef0123', PP_PASSWORD: 'dev', PP_DATA_DIR: join(work, 'data'),
    ...(process.env.PP_WS_DEBUG ? { 'Logging__LogLevel__Microsoft.AspNetCore': 'Information' } : {}),
  },
  stdio: process.env.PP_WS_DEBUG ? ['ignore', 'inherit', 'inherit'] : 'ignore',
});
const stopServer = () => {
  try { execFileSync('taskkill', ['/PID', String(host.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { host.kill(); }
};
process.on('exit', stopServer);
for (let i = 0; ; i++) {
  try { if ((await fetch(`${server}/api/server-info`)).ok) break; } catch { /* starting */ }
  if (i > 240) { stopServer(); throw new Error('The local server did not start.'); }
  await new Promise(r => setTimeout(r, 500));
}

const PROTOCOL = { 'X-PrivatePaste-Protocol': '2' };
async function call(method, path, { token, json, body } = {}) {
  const headers = { ...PROTOCOL };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (json) headers['Content-Type'] = 'application/json';
  const r = await fetch(server + path, { method, headers, body: json ? JSON.stringify(json) : body });
  if (!r.ok) throw new Error(`${method} ${path}: ${r.status}`);
  return r.status === 204 ? null : r.json();
}
const login = deviceName => call('POST', '/api/auth/login', { json: { username: 'owner', password: 'dev', deviceName, platform: deviceName === 'Edge' ? 'extension' : 'android' } });

// ---- the server: encryption and two devices ----
const vk = crypto.getRandomValues(new Uint8Array(32));
const vault = await e2e.importVaultKey(vk);
const owner = await login('Setup');
await call('PUT', '/api/vault', { token: owner.token, json: await e2e.wrapWithPassphrase(vault, 'a throwaway test passphrase') });
const extension = await login('Edge');
const phone = await login('Phone');

// ---- the extension ----
const ext = fileURLToPath(new URL('..', import.meta.url));
const out = join(work, 'ext');
execFileSync(process.execPath, [join(ext, 'build.mjs'), '--version', '0.0.1', '--fake-clipboard', '--out', out], { stdio: 'inherit' });
// Test only: the popup asks for the server's origin at sign-in; here nobody can click the prompt.
const manifestPath = join(out, 'manifest.json');
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
manifest.host_permissions = ['http://localhost/*', 'http://127.0.0.1/*'];
writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));

async function waitUntilChrome(worker) {
  for (let i = 0; i < 50; i++) {
    if (await worker.evaluate(() => typeof chrome !== 'undefined' && !!chrome.runtime?.id)) return;
    await new Promise(r => setTimeout(r, 100));
  }
  throw new Error('The service worker has no chrome API.');
}

const context = await chromium.launchPersistentContext(join(work, 'profile'), {
  channel: 'msedge',
  headless: false,
  args: [`--disable-extensions-except=${out}`, `--load-extension=${out}`],
});
try {
  const isOurs = w => w.url().startsWith('chrome-extension://') && w.url().endsWith('/sw.js');
  const sw = context.serviceWorkers().find(isOurs) ?? await context.waitForEvent('serviceworker', { predicate: isOurs });
  await waitUntilChrome(sw);

  await sw.evaluate(async ({ serverUrl, token, deviceId, vkBytes }) => {
    globalThis.__statuses = [];
    chrome.runtime.onMessage.addListener(m => { if (m?.type === 'engine.statusChanged') globalThis.__statuses.push(m.status); });

    // The vault as store.js keeps it: non-extractable subkeys.
    const raw = new Uint8Array(vkBytes);
    const base = await crypto.subtle.importKey('raw', raw, 'HKDF', false, ['deriveBits']);
    const derive = async info => new Uint8Array(await crypto.subtle.deriveBits(
      { name: 'HKDF', hash: 'SHA-256', salt: new Uint8Array(0), info: new TextEncoder().encode(info) }, base, 256));
    const enc = await derive('privatepaste v1 enc');
    const hash = await derive('privatepaste v1 hash');
    const id = await derive('privatepaste v1 key id');
    const keys = {
      keyId: Array.from(id.subarray(0, 8), b => b.toString(16).padStart(2, '0')).join(''),
      encKey: await crypto.subtle.importKey('raw', enc, 'AES-GCM', false, ['encrypt', 'decrypt']),
      hashKey: await crypto.subtle.importKey('raw', hash, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']),
    };
    const db = await new Promise((resolve, reject) => {
      const r = indexedDB.open('privatepaste', 1);
      r.onupgradeneeded = () => r.result.createObjectStore('kv');
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => reject(r.error);
    });
    await new Promise((resolve, reject) => {
      const tx = db.transaction('kv', 'readwrite');
      tx.objectStore('kv').put({ serverUrl, token, deviceId, deviceName: 'Edge' }, 'session');
      tx.objectStore('kv').put(keys, 'vault');
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
    });
    db.close();

    // Restart the offscreen document so the engine starts from that state.
    if ((await chrome.runtime.getContexts({ contextTypes: ['OFFSCREEN_DOCUMENT'] })).length) await chrome.offscreen.closeDocument();
    await chrome.offscreen.createDocument({ url: 'offscreen.html', reasons: ['CLIPBOARD'], justification: 'test' });
  }, { serverUrl: server, token: extension.token, deviceId: extension.deviceId, vkBytes: Array.from(vk) });

  const statuses = () => sw.evaluate(() => globalThis.__statuses.map(s => ({ state: s.state, link: s.link })));
  const waitFor = async (what, check, ms = 20000) => {
    const end = Date.now() + ms;
    while (Date.now() < end) {
      if (await check()) return;
      await new Promise(r => setTimeout(r, 200));
    }
    throw new Error(`Timed out waiting for ${what}; statuses: ${JSON.stringify(await statuses())}`);
  };

  await waitFor('Syncing', async () => (await statuses()).some(s => s.state === 'syncing'));
  const linked = (await statuses()).filter(s => s.state === 'syncing').at(-1).link;
  console.log('link:', linked);

  // Another device copies something.
  const text = `ws check ${Date.now()}`;
  const form = new FormData();
  const meta = {
    kind: 'text', keyId: vault.keyId, hash: await e2e.keyedHash(vault, await e2e.contentHashText(text)),
    payload: await e2e.sealPayload(vault, { text }), sizeBytes: new TextEncoder().encode(text).length,
    copiedAt: new Date().toISOString(), backfill: false,
  };
  form.append('meta', new Blob([JSON.stringify(meta)], { type: 'application/json' }), 'meta.json');
  const sentAt = Date.now();
  await call('POST', '/api/clips', { token: phone.token, body: form });
  await waitFor('the clip on the fake clipboard', async () =>
    (await sw.evaluate(() => chrome.runtime.sendMessage({ type: 'test.clipboard.get' })))?.text === text, 10000);
  console.log(`clip applied after ${Date.now() - sentAt} ms, link ${(await statuses()).at(-1).link}`);
  if (linked === 'socket') {
    console.log('PASS: the socket authenticated with the offered token and delivered the clip live.');
  } else {
    console.log('FAIL: the socket did not open; the engine polled /api/sync instead.');
    process.exitCode = 1;
  }
} finally {
  await context.close();
  stopServer();
  await new Promise(r => setTimeout(r, 1000));
  try { rmSync(work, { recursive: true, force: true }); } catch { /* Edge or the server may still hold a file */ }
}
