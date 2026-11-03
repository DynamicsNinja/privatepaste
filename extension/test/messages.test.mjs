import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handleEngineMessage, replyWith, urlKeeper, URL_LIFETIME_MS } from '../src/messages.js';
import { toBase64, fromBase64 } from '../src/core/base64.js';

// An engine double that records calls; `ready` settles like the real one's after start().
function fakeEngine(overrides = {}) {
  const calls = [];
  const record = name => async (...args) => { calls.push([name, ...args]); return overrides[name] ? overrides[name](...args) : { state: 'syncing' }; };
  return {
    calls,
    ready: Promise.resolve(),
    status: () => ({ state: 'syncing', serverUrl: 'http://localhost:1', deviceName: 'Edge', paused: false }),
    signIn: record('signIn'),
    unlock: record('unlock'),
    signOut: record('signOut'),
    setPaused: record('setPaused'),
    history: record('history'),
    copy: record('copy'),
    download: record('download'),
    historyVersion: () => 'abc:7',
  };
}

test('messages that are not for the engine get no answer', () => {
  const engine = fakeEngine();
  assert.equal(handleEngineMessage(engine, { type: 'test.clipboard.get' }), undefined);
  assert.equal(handleEngineMessage(engine, { type: 'engine.statusChanged', status: {} }), undefined);
  assert.equal(handleEngineMessage(engine, null), undefined);
  assert.equal(handleEngineMessage(engine, { type: 'engine.nope' }), undefined);
});

test('status waits for the stored state to load', async () => {
  let loaded;
  const engine = { ...fakeEngine(), ready: new Promise(r => { loaded = r; }) };
  let answered = false;
  const answer = handleEngineMessage(engine, { type: 'engine.status' }).then(a => { answered = true; return a; });
  await new Promise(r => setTimeout(r, 10));
  assert.equal(answered, false);
  loaded();
  assert.deepEqual(await answer, { ok: true, value: engine.status() });
});

test('sign in, unlock, sign out and pause pass their arguments through', async () => {
  const engine = fakeEngine();
  await handleEngineMessage(engine, { type: 'engine.signIn', serverUrl: 'http://s', username: 'owner', password: 'pw', deviceName: 'Edge' });
  await handleEngineMessage(engine, { type: 'engine.unlock', passphrase: 'a passphrase' });
  await handleEngineMessage(engine, { type: 'engine.setPaused', paused: true });
  await handleEngineMessage(engine, { type: 'engine.signOut' });
  assert.deepEqual(engine.calls, [
    ['signIn', 'http://s', 'owner', 'pw', 'Edge'],
    ['unlock', 'a passphrase'],
    ['setPaused', true],
    ['signOut'],
  ]);
});

test('an error comes back as its message, never as a throw', async () => {
  const engine = fakeEngine({ unlock: () => { throw new Error("That passphrase doesn't unlock this server's history."); } });
  assert.deepEqual(await handleEngineMessage(engine, { type: 'engine.unlock', passphrase: 'x' }),
    { ok: false, error: "That passphrase doesn't unlock this server's history." });
});

test('a network failure says the server could not be reached', async () => {
  const engine = fakeEngine({ signIn: () => { throw new TypeError('Failed to fetch'); } });
  assert.deepEqual(await handleEngineMessage(engine, { type: 'engine.signIn', serverUrl: 'http://s' }),
    { ok: false, error: "Can't reach the server." });
  const buggy = fakeEngine({ history: () => { throw new TypeError("Cannot read properties of undefined (reading 'x')"); } });
  assert.deepEqual(await handleEngineMessage(buggy, { type: 'engine.history' }),
    { ok: false, error: 'Something went wrong. Try again.' });
});

test('history rows carry thumbnails as base64, or none when not asked for', async () => {
  const png = new Uint8Array([137, 80, 78, 71, 0, 255]);
  const rows = [
    { id: 'a', kind: 'image', width: 2, height: 1, thumbPng: png, lastCopiedAt: 't' },
    { id: 'b', kind: 'text', text: 'hi', thumbPng: undefined, lastCopiedAt: 't' },
  ];
  const engine = fakeEngine({ history: () => ({ rows, nextCursor: 'server:' }) });
  const full = await handleEngineMessage(engine, { type: 'engine.history', pinned: true, cursor: 'server:x', query: 'q' });
  assert.deepEqual(engine.calls[0], ['history', { pinned: true, cursor: 'server:x', query: 'q', retryThumbs: false }]);
  assert.equal(full.ok, true);
  assert.equal(full.value.nextCursor, 'server:');
  assert.equal(full.value.rows[0].thumbPngBase64, toBase64(png));
  assert.equal('thumbPng' in full.value.rows[0], false);
  assert.equal(full.value.rows[1].thumbPngBase64, undefined);
  // JSON-safe: what chrome.runtime sends.
  assert.deepEqual(JSON.parse(JSON.stringify(full.value)), full.value);

  const light = await handleEngineMessage(engine, { type: 'engine.history', thumbs: false });
  assert.equal(light.value.rows[0].thumbPngBase64, undefined);
  assert.equal(light.value.rows[0].hasThumb, true);
});

test('retryThumbs passes through, and the history version answers without touching the history', async () => {
  const engine = fakeEngine({ history: () => ({ rows: [], nextCursor: null }) });
  await handleEngineMessage(engine, { type: 'engine.history', retryThumbs: true });
  assert.equal(engine.calls[0][1].retryThumbs, true);
  assert.deepEqual(await handleEngineMessage(engine, { type: 'engine.historyVersion' }), { ok: true, value: 'abc:7' });
  assert.equal(engine.calls.length, 1);
});

test('copy answers whether the engine wrote the clip', async () => {
  const engine = fakeEngine({ copy: id => id === 'text' });
  assert.deepEqual(await handleEngineMessage(engine, { type: 'engine.copy', id: 'text' }), { ok: true, value: true });
  assert.deepEqual(await handleEngineMessage(engine, { type: 'engine.copy', id: 'file' }), { ok: true, value: false });
});

test('copy of an image hands out a blob URL of the PNG the engine decrypted, for the popup to write', async () => {
  const png = new Uint8Array([137, 80, 78, 71, 1, 2]);
  const engine = fakeEngine({ copy: () => ({ png }) });
  const { urls, made } = fakeUrls();
  const answer = await handleEngineMessage(engine, { type: 'engine.copy', id: 'image' }, urls);
  assert.deepEqual(answer, { ok: true, value: { url: 'blob:test/1' } });
  assert.deepEqual(new Uint8Array(await made[0].arrayBuffer()), png);
  assert.equal(urls.issued.has('blob:test/1'), true, 'released like a download');
});

// Blob URLs without a browser: records what was made, revoked and scheduled.
function fakeUrls() {
  const made = [];
  const revoked = [];
  const timers = [];
  const urls = urlKeeper({
    create: blob => { made.push(blob); return `blob:test/${made.length}`; },
    revoke: url => revoked.push(url),
    setTimeout: (fn, ms) => timers.push({ fn, ms }),
  });
  return { urls, made, revoked, timers };
}

test('download hands out a blob URL of the decrypted bytes, never the bytes themselves', async () => {
  const bytes = new Uint8Array(70000).map((_, i) => i % 251);
  const engine = fakeEngine({ download: () => ({ bytes, name: 'report.pdf', mime: 'application/pdf' }) });
  const { urls, made } = fakeUrls();
  const answer = await handleEngineMessage(engine, { type: 'engine.download', id: 'f', n: 1 }, urls);
  assert.deepEqual(engine.calls[0], ['download', 'f', 1]);
  assert.deepEqual(answer, { ok: true, value: { url: 'blob:test/1', name: 'report.pdf', mime: 'application/pdf' } });
  assert.equal(made[0].type, 'application/octet-stream');
  assert.deepEqual(new Uint8Array(await made[0].arrayBuffer()), bytes);
});

test('a file bigger than a runtime message may be still answers with a short message', async () => {
  const bytes = new Uint8Array(65 * 1024 * 1024);   // over Chrome's 64 MiB message cap, even before base64
  const engine = fakeEngine({ download: () => ({ bytes, name: 'big.iso' }) });
  const { urls, made } = fakeUrls();
  const answer = await handleEngineMessage(engine, { type: 'engine.download', id: 'f', n: 0 }, urls);
  assert.ok(JSON.stringify(answer).length < 200);
  assert.equal(made[0].size, bytes.length);
});

test('a download URL is revoked on release, or after a minute, once', async () => {
  const engine = fakeEngine({ download: () => ({ bytes: new Uint8Array([1]), name: 'a' }) });
  const { urls, revoked, timers } = fakeUrls();
  const { value: { url } } = await handleEngineMessage(engine, { type: 'engine.download', id: 'f' }, urls);
  assert.equal(timers[0].ms, URL_LIFETIME_MS);
  assert.deepEqual(await handleEngineMessage(engine, { type: 'engine.release', url }, urls), { ok: true, value: true });
  assert.deepEqual(revoked, [url]);
  timers[0].fn();
  assert.deepEqual(revoked, [url], 'the timer finds it already released');

  const second = (await handleEngineMessage(engine, { type: 'engine.download', id: 'f' }, urls)).value.url;
  timers[1].fn();
  assert.deepEqual(revoked, [url, second]);
});

test('release ignores URLs it did not hand out', async () => {
  const { urls, revoked } = fakeUrls();
  assert.deepEqual(await handleEngineMessage(fakeEngine(), { type: 'engine.release', url: 'blob:other/1' }, urls), { ok: true, value: false });
  assert.deepEqual(revoked, []);
});

test('an answer that cannot be sent becomes a short failure, so the popup is not left waiting', async () => {
  const sent = [];
  let first = true;
  const reply = value => {
    if (first) { first = false; throw new Error('Message length exceeded maximum allowed length.'); }
    sent.push(value);
  };
  await replyWith(Promise.resolve({ ok: true, value: 'huge' }), reply);
  assert.deepEqual(sent, [{ ok: false, error: 'Something went wrong. Try again.' }]);
  await replyWith(Promise.resolve({ ok: true }), () => { throw new Error('port closed'); });   // nothing thrown out
});

test('base64 round trips every byte value', () => {
  const all = Uint8Array.from({ length: 256 }, (_, i) => i);
  assert.deepEqual(fromBase64(toBase64(all)), all);
  assert.equal(toBase64(new Uint8Array()), '');
  assert.equal(toBase64(new TextEncoder().encode('hello')), 'aGVsbG8=');
});
