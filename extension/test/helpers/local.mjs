// Shared plumbing of the hand-run Edge checks (*.playwright.mjs): a LOCAL server (PP_PASSWORD=dev, a throwaway
// PP_DATA_DIR, a free localhost port), a fake-clipboard build of the extension, and Edge with it loaded. Never the live
// server, never the OS clipboard.

import { execFileSync, spawn } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(new URL('../../../scripts/readme/package.json', import.meta.url));
export const { chromium } = require('playwright');

export const repo = fileURLToPath(new URL('../../..', import.meta.url));
export const PROTOCOL = { 'X-PrivatePaste-Protocol': '2' };

export const freePort = () => new Promise((resolve, reject) => {
  const s = createServer().listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); });
  s.on('error', reject);
});

/**
 * Starts the server on `port` over `dataDir` (created if missing) and waits until it answers. Starting it again on the
 * same port and data dir is a restart. `verbose` shows the server's request log.
 * @returns {Promise<{url: string, stop: () => Promise<void>}>}
 */
export async function startServer({ dataDir, port, verbose = false }) {
  port ??= await freePort();
  const url = `http://localhost:${port}`;
  if (/ficdev\.xyz/i.test(url)) throw new Error('Never run this against the live server.');
  mkdirSync(dataDir, { recursive: true });
  const host = spawn('dotnet', ['run', '--project', join(repo, 'src', 'PrivatePaste.Server'), '--no-launch-profile', '--urls', url], {
    env: {
      ...process.env, PP_DB_KEY: '0123456789abcdef0123', PP_PASSWORD: 'dev', PP_DATA_DIR: dataDir,
      ...(verbose ? { 'Logging__LogLevel__Microsoft.AspNetCore': 'Information' } : {}),
    },
    stdio: verbose ? ['ignore', 'inherit', 'inherit'] : 'ignore',
  });
  let stopped = false;
  const kill = () => {
    if (stopped) return;
    stopped = true;
    try { execFileSync('taskkill', ['/PID', String(host.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { host.kill(); }
  };
  process.on('exit', kill);
  const stop = async () => {
    kill();
    // Down means nothing answers on the port any more (taskkill returns before the listener is gone).
    for (let i = 0; i < 100; i++) {
      try { await fetch(`${url}/api/server-info`, { headers: PROTOCOL }); } catch { return; }
      await new Promise(r => setTimeout(r, 100));
    }
    throw new Error('The local server did not stop.');
  };
  const end = Date.now() + 240000;
  for (;;) {
    try {
      if ((await fetch(`${url}/api/server-info`, { headers: PROTOCOL })).ok) return { url, stop };
    } catch { /* not up yet */ }
    if (Date.now() > end) { kill(); throw new Error('The local server did not start.'); }
    await new Promise(r => setTimeout(r, 300));
  }
}

/** The REST API as another client: `call(method, path, {token, json, body})` and `login(deviceName, platform)`. */
export function apiClient(url) {
  async function call(method, path, { token, json, body } = {}) {
    const headers = { ...PROTOCOL };
    if (token) headers.Authorization = `Bearer ${token}`;
    if (json) headers['Content-Type'] = 'application/json';
    const r = await fetch(url + path, { method, headers, body: json ? JSON.stringify(json) : body });
    if (!r.ok) throw new Error(`${method} ${path}: ${r.status}`);
    return r.status === 204 ? null : r.json();
  }
  const login = (deviceName, platform) =>
    call('POST', '/api/auth/login', { json: { username: 'owner', password: 'dev', deviceName, platform } });
  return { call, login };
}

/**
 * Builds the extension into `out` with the FAKE clipboard, plus a test-only host permission for localhost: the popup
 * asks for the server's origin at sign-in, and nobody can click that prompt here.
 */
export function buildFakeExtension(out) {
  execFileSync(process.execPath, [join(repo, 'extension', 'build.mjs'), '--version', '0.0.1', '--fake-clipboard', '--out', out], { stdio: 'inherit' });
  const manifestPath = join(out, 'manifest.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  manifest.host_permissions = ['http://localhost/*', 'http://127.0.0.1/*'];
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));
}

/** Headed Edge on the profile `profile` with only the extension in `out` loaded. */
export const launchEdge = (profile, out, options = {}) => chromium.launchPersistentContext(profile, {
  channel: 'msedge',
  headless: false,
  args: [`--disable-extensions-except=${out}`, `--load-extension=${out}`],
  ...options,
});

/** The extension's service worker, once its chrome API is there. */
export async function extensionWorker(context) {
  const isOurs = w => w.url().startsWith('chrome-extension://') && w.url().endsWith('/sw.js');
  const sw = context.serviceWorkers().find(isOurs) ?? await context.waitForEvent('serviceworker', { predicate: isOurs });
  for (let i = 0; i < 50; i++) {
    if (await sw.evaluate(() => typeof chrome !== 'undefined' && !!chrome.runtime?.id)) return sw;
    await new Promise(r => setTimeout(r, 100));
  }
  throw new Error('The service worker has no chrome API.');
}

/** Polls `check` every 100 ms until it returns something truthy, which it returns; `what` names it in the timeout. */
export async function waitFor(what, check, ms = 20000) {
  const end = Date.now() + ms;
  for (;;) {
    const value = await check();
    if (value) return value;
    if (Date.now() > end) throw new Error(`Timed out waiting for ${what}.`);
    await new Promise(r => setTimeout(r, 100));
  }
}
