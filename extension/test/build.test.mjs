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
  assert.deepEqual(manifest.permissions, ['offscreen', 'clipboardRead', 'clipboardWrite', 'alarms', 'declarativeNetRequestWithHostAccess']);
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
