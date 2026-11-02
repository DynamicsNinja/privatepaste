import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handleEngineMessage } from '../src/messages.js';
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
    { ok: false, error: "Couldn't reach the server. Check the address." });
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
  assert.deepEqual(engine.calls[0], ['history', { pinned: true, cursor: 'server:x', query: 'q' }]);
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

test('copy answers whether the engine wrote the clip', async () => {
  const engine = fakeEngine({ copy: id => id === 'text' });
  assert.deepEqual(await handleEngineMessage(engine, { type: 'engine.copy', id: 'text' }), { ok: true, value: true });
  assert.deepEqual(await handleEngineMessage(engine, { type: 'engine.copy', id: 'image' }), { ok: true, value: false });
});

test('download sends the bytes as base64 with the safe name', async () => {
  const bytes = new Uint8Array(70000).map((_, i) => i % 251);
  const engine = fakeEngine({ download: () => ({ bytes, name: 'report.pdf', mime: 'application/pdf' }) });
  const answer = await handleEngineMessage(engine, { type: 'engine.download', id: 'f', n: 1 });
  assert.deepEqual(engine.calls[0], ['download', 'f', 1]);
  assert.equal(answer.value.name, 'report.pdf');
  assert.equal(answer.value.mime, 'application/pdf');
  assert.deepEqual(fromBase64(answer.value.bytesBase64), bytes);
});

test('base64 round trips every byte value', () => {
  const all = Uint8Array.from({ length: 256 }, (_, i) => i);
  assert.deepEqual(fromBase64(toBase64(all)), all);
  assert.equal(toBase64(new Uint8Array()), '');
  assert.equal(toBase64(new TextEncoder().encode('hello')), 'aGVsbG8=');
});
