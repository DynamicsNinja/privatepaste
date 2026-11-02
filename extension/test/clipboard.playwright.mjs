// Real-Edge check of the offscreen clipboard, run by hand (not in CI, and not by `node --test`):
//
//   node extension/test/clipboard.playwright.mjs [--seconds 60]
//
// Builds the extension with the REAL clipboard into a temp folder, adds a test-only probe to its offscreen document,
// and loads it into Edge (headed, minimized, throwaway profile). Then it checks:
//   1. the extension reads the current clipboard text exactly: its SHA-256 equals that of `Get-Clipboard -Raw` with
//      CRLF -> LF (skipped when the clipboard holds no text);
//      when the clipboard holds a PNG (a screenshot, say), the extension's PNG equals its PNG format byte for byte;
//   2. Argon2 (e2e.js + the vendored hash-wasm) loads and runs under the manifest's CSP;
//   3. the CPU cost of polling once a second: the share of wall time spent inside the poll steps.
// It only READS the clipboard: the owner's desktop app syncs it to a live server. It never prints clipboard content,
// only whether the hashes match and lengths. Uses Playwright from scripts/readme (run `npm ci` there once).

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(new URL('../../scripts/readme/package.json', import.meta.url));
const { chromium } = require('playwright');

const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const seconds = Number(arg('seconds', 60));

const ext = fileURLToPath(new URL('..', import.meta.url));
const work = mkdtempSync(join(tmpdir(), 'pp-ext-clip-'));
const out = join(work, 'ext');
const profile = join(work, 'profile');
execFileSync(process.execPath, [join(ext, 'build.mjs'), '--version', '0.0.1', '--out', out], { stdio: 'inherit' });

// Test-only probe, added to the built offscreen document. It imports the same module instances the page runs.
writeFileSync(join(out, 'test-probe.js'), `
import { readClipboard } from './platform/clipboard.js';
import { startPolling } from './offscreen.js';
import { _argon2Key } from './js/e2e.js';

const hex = async bytes => Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), b => b.toString(16).padStart(2, '0')).join('');
let poller;
let pollStarted;

chrome.runtime.onMessage.addListener((m, _s, reply) => {
  (async () => {
    if (m.type === 'probe.ping') return { ok: true };
    if (m.type === 'probe.read') {
      const item = await readClipboard();
      if (!item) return { ok: false };
      return { ok: true, textSha256: item.text ? await hex(new TextEncoder().encode(item.text)) : null, textLength: item.text?.length ?? 0, pngSha256: item.png ? await hex(item.png) : null, pngLength: item.png?.length ?? 0 };
    }
    if (m.type === 'probe.argon2') {
      const t = performance.now();
      const key = await _argon2Key('probe passphrase', new Uint8Array(16).fill(7));
      return { ok: true, length: key.length, ms: Math.round(performance.now() - t) };
    }
    if (m.type === 'probe.pollStart') {
      poller = startPolling(() => {}, 1000);
      pollStarted = performance.now();
      return { ok: true };
    }
    if (m.type === 'probe.pollStats') {
      const { reads, busyMs } = poller.stats();
      return { ok: true, reads, busyMs, elapsedMs: performance.now() - pollStarted };
    }
  })().then(reply, e => reply({ ok: false, error: String(e?.message ?? e) }));
  return true;
});
`);
const html = join(out, 'offscreen.html');
writeFileSync(html, readFileSync(html, 'utf8').replace('</head>', '  <script type="module" src="test-probe.js"></script>\n</head>'));

const ps = command => execFileSync('powershell', ['-NoProfile', '-STA', '-Command', command], { encoding: 'utf8' }).trim();
const osClipboardSha256 = () => {
  const b64 = ps("$t = Get-Clipboard -Raw; if ($null -eq $t) { '' } else { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($t)) }");
  if (!b64) return null;
  const text = Buffer.from(b64, 'base64').toString('utf8').replace(/\r\n/g, '\n');
  return createHash('sha256').update(text, 'utf8').digest('hex');
};
// The PNG format apps put on the clipboard (screenshots, browsers), as stored.
const osPngSha256 = () => {
  const b64 = ps("Add-Type -AssemblyName System.Windows.Forms; $d = [Windows.Forms.Clipboard]::GetData('PNG'); if ($d -is [IO.MemoryStream]) { [Convert]::ToBase64String($d.ToArray()) } else { '' }");
  return b64 ? createHash('sha256').update(Buffer.from(b64, 'base64')).digest('hex') : null;
};

const ctx = await chromium.launchPersistentContext(profile, {
  channel: 'msedge',
  headless: false,
  args: [`--disable-extensions-except=${out}`, `--load-extension=${out}`, '--start-minimized'],
});
let failed = false;
try {
  let [sw] = ctx.serviceWorkers();
  sw ??= await ctx.waitForEvent('serviceworker');
  const send = message => sw.evaluate(m => chrome.runtime.sendMessage(m), message);

  // The worker opens the offscreen document on install; wait until the probe answers.
  for (let i = 0; ; i++) {
    const pong = await send({ type: 'probe.ping' }).catch(() => null);
    if (pong?.ok) break;
    if (i > 50) throw new Error('The offscreen document did not start.');
    await new Promise(r => setTimeout(r, 200));
  }
  const contexts = await sw.evaluate(() => chrome.runtime.getContexts({ contextTypes: ['OFFSCREEN_DOCUMENT'] }));
  assert.equal(contexts.length, 1, 'one offscreen document');
  console.log('offscreen document: open');

  // 1. Read check. Read the OS clipboard before and after, so a copy made meanwhile doesn't count as a mismatch.
  const before = osClipboardSha256();
  const read = await send({ type: 'probe.read' });
  const after = osClipboardSha256();
  if (!read.ok) throw new Error('The extension could not read the clipboard.');
  const png = osPngSha256();
  if (png && read.pngSha256) {
    assert.equal(read.pngSha256, png, `extension's PNG (${read.pngLength} bytes) differs from the clipboard's PNG format`);
    console.log(`image read check: PASS (${read.pngLength}-byte PNG matches the clipboard's PNG format byte for byte)`);
  } else if (png) assert.fail('the clipboard holds a PNG but the extension read none');
  if (before === null) console.log('read check: SKIPPED, the clipboard holds no text');
  else if (before !== after) console.log('read check: SKIPPED, the clipboard changed during the check; run again');
  else {
    assert.equal(read.textSha256, before, `extension's read (length ${read.textLength}) differs from Get-Clipboard`);
    console.log(`read check: PASS (text matches Get-Clipboard, ${read.textLength} chars${read.pngLength ? `, plus a ${read.pngLength}-byte PNG` : ''})`);
  }

  // 2. Argon2 under the extension's CSP.
  const argon = await send({ type: 'probe.argon2' });
  assert.ok(argon.ok, `Argon2 failed: ${argon.error}`);
  assert.equal(argon.length, 32);
  console.log(`argon2: PASS (32-byte key in ${argon.ms} ms)`);

  // 3. Polling cost.
  await send({ type: 'probe.pollStart' });
  console.log(`polling for ${seconds} s...`);
  await new Promise(r => setTimeout(r, seconds * 1000));
  const stats = await send({ type: 'probe.pollStats' });
  const busy = stats.busyMs / stats.elapsedMs;
  console.log(`polling: ${stats.reads} reads in ${(stats.elapsedMs / 1000).toFixed(1)} s, ${stats.busyMs.toFixed(1)} ms busy, ` +
    `${(busy * 100).toFixed(3)}% of one core, ${(stats.busyMs / stats.reads).toFixed(2)} ms per read`);
} catch (e) {
  failed = true;
  console.error(e.message);
} finally {
  await ctx.close();
  rmSync(work, { recursive: true, force: true });
}
process.exit(failed ? 1 : 0);
