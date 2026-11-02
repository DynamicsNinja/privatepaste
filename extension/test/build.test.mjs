import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ext = fileURLToPath(new URL('..', import.meta.url));
const wwwroot = fileURLToPath(new URL('../../src/PrivatePaste.Server/wwwroot/', import.meta.url));

function build(...args) {
  const out = mkdtempSync(join(tmpdir(), 'pp-ext-build-'));
  execFileSync(process.execPath, [join(ext, 'build.mjs'), '--out', out, ...args], { stdio: 'pipe' });
  return out;
}

test('build writes the manifest with the given version, permissions and CSP', t => {
  const out = build('--version', '1.2.3');
  t.after(() => rmSync(out, { recursive: true, force: true }));
  const manifest = JSON.parse(readFileSync(join(out, 'manifest.json'), 'utf8'));
  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.version, '1.2.3');
  assert.deepEqual(manifest.permissions, ['offscreen', 'clipboardRead', 'clipboardWrite', 'alarms']);
  assert.deepEqual(manifest.optional_host_permissions, ['http://*/*', 'https://*/*']);
  assert.equal(manifest.content_security_policy.extension_pages, "script-src 'self' 'wasm-unsafe-eval'; object-src 'self'");
  assert.equal(manifest.background.service_worker, 'sw.js');
});

test('a prerelease version keeps the numbers in version and the full tag in version_name', t => {
  const out = build('--version', '0.6.0-rc1');
  t.after(() => rmSync(out, { recursive: true, force: true }));
  const manifest = JSON.parse(readFileSync(join(out, 'manifest.json'), 'utf8'));
  assert.equal(manifest.version, '0.6.0');
  assert.equal(manifest.version_name, '0.6.0-rc1');
});

test('build rejects a version Edge would not accept', () => {
  assert.throws(() => build('--version', 'v1.2'));
});

test('build copies e2e.js and the Argon2 library unchanged', t => {
  const out = build('--version', '1.2.3');
  t.after(() => rmSync(out, { recursive: true, force: true }));
  assert.deepEqual(readFileSync(join(out, 'js', 'e2e.js')), readFileSync(join(wwwroot, 'js', 'e2e.js')));
  for (const name of ['argon2.umd.min.js', 'LICENSE'])
    assert.deepEqual(readFileSync(join(out, 'lib', 'hash-wasm', name)), readFileSync(join(wwwroot, 'lib', 'hash-wasm', name)));
});

test('a release build ships the real clipboard and no fake', t => {
  const out = build('--version', '1.2.3');
  t.after(() => rmSync(out, { recursive: true, force: true }));
  assert.deepEqual(readFileSync(join(out, 'platform', 'clipboard.js')), readFileSync(join(ext, 'src', 'platform', 'clipboard.js')));
  assert.equal(existsSync(join(out, 'platform', 'fake-clipboard.js')), false);
  for (const name of ['sw.js', 'offscreen.html', 'offscreen.js', 'engine-host.js'])
    assert.ok(existsSync(join(out, name)), name);
});

test('--fake-clipboard swaps in the fake clipboard', t => {
  const out = build('--version', '1.2.3', '--fake-clipboard');
  t.after(() => rmSync(out, { recursive: true, force: true }));
  assert.deepEqual(readFileSync(join(out, 'platform', 'clipboard.js')), readFileSync(join(ext, 'src', 'platform', 'fake-clipboard.js')));
  assert.equal(existsSync(join(out, 'platform', 'fake-clipboard.js')), false);
});

test('the manifest opens the popup from the toolbar, with icons that exist in the build', t => {
  const out = build('--version', '1.2.3');
  t.after(() => rmSync(out, { recursive: true, force: true }));
  const manifest = JSON.parse(readFileSync(join(out, 'manifest.json'), 'utf8'));
  const icons = { 16: 'icons/icon-16.png', 32: 'icons/icon-32.png', 48: 'icons/icon-48.png', 128: 'icons/icon-128.png' };
  assert.deepEqual(manifest.action, { default_popup: 'popup.html', default_title: 'PrivatePaste', default_icon: icons });
  assert.deepEqual(manifest.icons, icons);
  for (const path of [manifest.action.default_popup, ...Object.values(icons)]) assert.ok(existsSync(join(out, path)), path);
});

test('build copies the Sora fonts from the web UI, the only copy in the repo', t => {
  const out = build('--version', '1.2.3');
  t.after(() => rmSync(out, { recursive: true, force: true }));
  for (const name of ['sora-latin.woff2', 'sora-latin-ext.woff2', 'OFL.txt'])
    assert.deepEqual(readFileSync(join(out, 'fonts', name)), readFileSync(join(wwwroot, 'fonts', name)));
  assert.equal(existsSync(join(ext, 'src', 'fonts')), false);
  // popup.css points at them.
  for (const name of ['sora-latin.woff2', 'sora-latin-ext.woff2'])
    assert.ok(readFileSync(join(out, 'popup.css'), 'utf8').includes(`url("fonts/${name}")`), name);
});
