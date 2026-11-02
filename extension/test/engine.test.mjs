import './support/register.mjs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fakeClock, fakeClipboard, fakeSockets, fakeServer, settle, until } from './support/fakes.mjs';

const e2e = await import('../../src/PrivatePaste.Server/wwwroot/js/e2e.js');
const { createEngine, MESSAGES, BACKOFF_MS, POLL_MS } = await import('../src/core/engine.js');
const { openStore, memoryBackend } = await import('../src/core/store.js');
const { ApiError } = await import('../src/core/api.js');
const { encodePng } = await import('../src/core/image.js');

const vectors = JSON.parse(readFileSync(new URL('../../docs/e2e-test-vectors.json', import.meta.url), 'utf8'));
const vk = () => Uint8Array.from(Buffer.from(vectors.vaultKey.vk, 'hex'));
const keys = await e2e.importVaultKey(vk());
const SERVER = 'http://localhost:5202';
const OWN = 'dev-own';
const OTHER = 'dev-phone';

const iso = ms => new Date(ms).toISOString();

/** A clip as the server sends it, sealed under the test vault. */
async function clip({ id, seq, at, text = `text ${id}`, kind = 'text', device = OTHER, payload, extra = {} }) {
  return {
    id, seq, kind,
    keyId: keys.keyId,
    hash: 'f'.repeat(64),
    payload: await e2e.sealPayload(keys, payload ?? { text }),
    sizeBytes: 1,
    sourceDeviceId: device,
    sourceDeviceName: device === OWN ? 'Edge' : 'Phone',
    sourceDevicePlatform: device === OWN ? 'extension' : 'android',
    sourceUserName: null,
    lastCopiedAt: iso(at),
    pinnedAt: null,
    hasThumb: false,
    blobCount: 0,
    ...extra,
  };
}

/** A signed-in, unlocked engine over a memory store (unless `signedIn` is false). */
async function setup({ signedIn = true, state = {}, store = openStore(memoryBackend()), wrap = { keyId: keys.keyId } } = {}) {
  const clock = fakeClock();
  const server = fakeServer({ wrap, now: () => clock.now() });
  const clipboard = fakeClipboard();
  const ws = fakeSockets();
  if (signedIn) {
    await store.setSession({ serverUrl: SERVER, token: 'tok', deviceId: OWN, deviceName: 'Edge' });
    await store.setVault({ raw: vk() });
    await store.setState(state);
  }
  const statuses = [];
  const engine = createEngine({
    api: server.api, store, clipboard, clock, WebSocket: ws.FakeWebSocket, onStatus: s => statuses.push(s),
  });
  return { engine, store, clock, server, clipboard, ws, statuses };
}

/** Starts the engine and waits until its socket is open and it says Syncing. */
async function connected(env) {
  await env.engine.start();
  await until(() => env.engine.status().state === 'syncing' && env.ws.open().length === 1, 'syncing');
  return env.ws.open()[0];
}

const live = async (socket, wire, isLive = true) => {
  socket.receive({ type: 'clip.created', clip: wire, live: isLive });
  await settle(30);
};

test('local copy uploads', async () => {
  const env = await setup();
  await connected(env);
  await env.engine.onLocalChange({ text: 'hello' });
  assert.equal(env.server.uploads.length, 1);
  const { meta } = env.server.uploads[0];
  assert.equal(meta.kind, 'text');
  assert.equal(meta.backfill, false);
  assert.equal(meta.keyId, keys.keyId);
  assert.deepEqual(await e2e.openPayload(keys, meta.payload), { text: 'hello' });
  assert.equal((await env.store.getCache())[0].id, 'up1');
});

test('non-live clip is not applied', async () => {
  const env = await setup();
  const socket = await connected(env);
  const at = env.clock.now() - 60000;
  await live(socket, await clip({ id: 'b1', seq: 1, at }), false);
  await live(socket, await clip({ id: 'b1', seq: 2, at, extra: { pinnedAt: iso(env.clock.now()) } }), false);
  assert.deepEqual(env.clipboard.written, []);
  const cached = await env.store.getCache();
  assert.equal(cached.length, 1);
  assert.equal(cached[0].seq, 2);
  assert.ok(cached[0].pinnedAt);
});

test('catch-up applies only the newest', async () => {
  const lastAppliedAt = Date.parse('2026-11-02T09:00:00Z');
  const env = await setup({ state: { lastAppliedAt, highestSeq: 10 } });
  env.server.syncPages.push({
    clips: [
      await clip({ id: 'a', seq: 11, at: lastAppliedAt + 1000, text: 'first' }),
      await clip({ id: 'c', seq: 13, at: lastAppliedAt + 3000, text: 'third' }),
      await clip({ id: 'b', seq: 12, at: lastAppliedAt + 2000, text: 'second' }),
    ],
    deletedIds: [], latestSeq: 13, hasMore: false,
  });
  await connected(env);
  assert.deepEqual(env.clipboard.written, ['third']);
  assert.equal((await env.store.getState()).lastAppliedAt, lastAppliedAt + 3000);
  assert.equal((await env.store.getState()).highestSeq, 13);
});

test('catch-up applies nothing that is not newer than the last applied clip', async () => {
  const lastAppliedAt = Date.parse('2026-11-02T09:00:00Z');
  const env = await setup({ state: { lastAppliedAt, highestSeq: 10 } });
  env.server.syncPages.push({
    clips: [await clip({ id: 'a', seq: 11, at: lastAppliedAt - 1000 }), await clip({ id: 'b', seq: 12, at: lastAppliedAt })],
    deletedIds: [], latestSeq: 12, hasMore: false,
  });
  await connected(env);
  assert.deepEqual(env.clipboard.written, []);
  assert.equal((await env.store.getCache()).length, 2);
});

test('the first catch-up after sign-in is history only', async () => {
  const env = await setup();
  const at = env.clock.now() - 5000;
  env.server.syncPages.push({ clips: [await clip({ id: 'a', seq: 1, at })], deletedIds: [], latestSeq: 1, hasMore: false });
  await connected(env);
  assert.deepEqual(env.clipboard.written, []);
  assert.equal((await env.store.getState()).lastAppliedAt, at);
});

test('catch-up follows hasMore and applies deletions', async () => {
  const env = await setup({ state: { lastAppliedAt: 0 } });
  env.server.syncPages.push(
    { clips: [await clip({ id: 'a', seq: 1, at: 1000 }), await clip({ id: 'b', seq: 2, at: 2000 })], deletedIds: [], latestSeq: 5, hasMore: true },
    { clips: [await clip({ id: 'c', seq: 4, at: 3000 })], deletedIds: ['a'], latestSeq: 5, hasMore: false },
  );
  await connected(env);
  assert.deepEqual(env.server.calls.filter(c => c.name === 'sync').map(c => c.args[0]).slice(0, 2), [0, 2]);
  assert.deepEqual((await env.store.getCache()).map(c => c.id), ['c', 'b']);
  assert.equal((await env.store.getState()).highestSeq, 5);
});

test('file clip is never applied', async () => {
  const env = await setup();
  const socket = await connected(env);
  await live(socket, await clip({
    id: 'f1', seq: 1, at: env.clock.now() + 1000, kind: 'file',
    payload: { files: [{ name: 'a.txt', size: 1 }] }, extra: { blobCount: 1 },
  }));
  assert.deepEqual(env.clipboard.written, []);
  assert.deepEqual(env.clipboard.images, []);
  assert.equal((await env.store.getCache())[0].id, 'f1');
});

test('unknown kind is never applied', async () => {
  const env = await setup();
  const socket = await connected(env);
  await live(socket, await clip({ id: 'u1', seq: 1, at: env.clock.now() + 1000, kind: 'richtext' }));
  assert.deepEqual(env.clipboard.written, []);
  const { rows } = await env.engine.history();
  assert.equal(rows[0].kind, 'unknown');
});

test('incoming image is history only', async () => {
  const env = await setup();
  const socket = await connected(env);
  const before = (await env.store.getState()).lastAppliedAt;
  await live(socket, await clip({
    id: 'i1', seq: 1, at: env.clock.now() + 1000, kind: 'image', payload: { width: 1, height: 1 },
    extra: { blobCount: 1, hasThumb: true },
  }));
  assert.deepEqual(env.clipboard.written, []);
  assert.deepEqual(env.clipboard.images, []);
  assert.equal(env.server.count('blob'), 0);
  assert.equal((await env.store.getState()).lastAppliedAt, before);
  assert.equal((await env.store.getCache())[0].id, 'i1');
});

test('queued copies backfill', async () => {
  const env = await setup();
  const socket = await connected(env);
  env.server.uploadErrors.push(new TypeError('fetch failed'));
  const copiedAt = env.clock.now();
  await env.engine.onLocalChange({ text: 'copied offline' });
  assert.equal((await env.store.queue()).length, 1);
  assert.equal(env.server.stored.length, 0);

  socket.close(1006);
  await until(() => env.engine.status().state === 'offline', 'offline');
  await env.clock.advance(BACKOFF_MS[0]);
  await until(() => env.server.stored.length === 1, 'the backfill');
  const { meta } = env.server.uploads[1];
  assert.equal(meta.backfill, true);
  assert.equal(meta.copiedAt, iso(copiedAt));
  assert.deepEqual(await e2e.openPayload(keys, meta.payload), { text: 'copied offline' });
  assert.equal((await env.store.queue()).length, 0);
  assert.equal(env.server.count('serverInfo'), 2);   // read again on the reconnect
});

test('a queued copy keeps a newer incoming clip off the clipboard until it is sent', async () => {
  const env = await setup({ state: { lastAppliedAt: 0 } });
  await env.store.enqueue({ copiedAt: Date.now(), kind: 'text', sealed: await e2e.encryptBlob(keys, new TextEncoder().encode('q')) });
  env.server.uploadErrors.push(new ApiError(503, null, null));
  env.server.syncPages.push({ clips: [await clip({ id: 'a', seq: 1, at: env.clock.now() + 5000 })], deletedIds: [], latestSeq: 1, hasMore: false });
  await connected(env);
  assert.deepEqual(env.clipboard.written, []);
  assert.equal((await env.store.queue())[0].attempts, 1);
});

test('410 drops quietly', async () => {
  const env = await setup();
  await env.store.enqueue({ copiedAt: Date.now() - 86400000, kind: 'text', sealed: await e2e.encryptBlob(keys, new TextEncoder().encode('old')) });
  env.server.uploadErrors.push(new ApiError(410, 'Cleared', null));
  await connected(env);
  assert.equal(env.server.uploads.length, 1);
  assert.equal(env.server.uploads[0].meta.backfill, true);
  assert.deepEqual(await env.store.queue(), []);
  assert.equal(env.engine.status().state, 'syncing');
  assert.equal(env.engine.status().message, undefined);
});

test('409 Encryption was reset relocks', async () => {
  const env = await setup();
  const socket = await connected(env);
  await live(socket, await clip({ id: 'x', seq: 1, at: env.clock.now() - 1000 }), false);
  await env.store.enqueue({ copiedAt: 1, kind: 'text', sealed: new Uint8Array([2, 0]) });
  env.server.uploadErrors.push(new ApiError(409, 'Encryption was reset', MESSAGES.reset));
  await env.engine.onLocalChange({ text: 'after the reset' });
  await until(() => env.engine.status().state === 'locked', 'locked');
  assert.equal(env.engine.status().message, MESSAGES.reset);
  assert.equal(await env.store.getVault(), null);
  assert.deepEqual(await env.store.getCache(), []);
  assert.deepEqual(await env.store.queue(), []);
  assert.ok(await env.store.getSession());
  assert.equal(socket.readyState, 3);
});

test('vault.reset relocks', async () => {
  const env = await setup();
  const socket = await connected(env);
  await live(socket, await clip({ id: 'x', seq: 1, at: env.clock.now() - 1000 }), false);
  socket.receive({ type: 'vault.reset' });
  await until(() => env.engine.status().state === 'locked', 'locked');
  assert.equal(env.engine.status().message, MESSAGES.reset);
  assert.equal(await env.store.getVault(), null);
  assert.deepEqual(await env.store.getCache(), []);
  // After a restart it still asks for the passphrase, with the reason.
  const again = createEngine({ api: env.server.api, store: env.store, clipboard: env.clipboard, clock: env.clock, WebSocket: env.ws.FakeWebSocket });
  await again.start();
  assert.deepEqual([again.status().state, again.status().message], ['locked', MESSAGES.reset]);
});

test('a vault with another key id at connect relocks', async () => {
  const env = await setup({ wrap: { keyId: '0123456789abcdef' } });
  await env.engine.start();
  await until(() => env.engine.status().state === 'locked', 'locked');
  assert.equal(env.engine.status().message, MESSAGES.reset);
});

test('revoked close signs out', async () => {
  const env = await setup();
  const socket = await connected(env);
  socket.close(1008);
  await until(() => env.engine.status().state === 'signedOut', 'signed out');
  assert.equal(await env.store.getSession(), null);
  assert.equal(await env.store.getVault(), null);
  await env.clock.advance(60000);
  assert.equal(env.ws.sockets.length, 1);   // no reconnect
});

test('a 401 signs out', async () => {
  const env = await setup();
  env.server.vaultError = new ApiError(401, 'Unauthorized', null);
  await env.engine.start();
  await until(() => env.engine.status().state === 'signedOut', 'signed out');
});

test('server-info 404 is serverTooOld', async () => {
  const env = await setup();
  env.server.infoError = new ApiError(404, 'Not Found', null);
  await env.engine.start();
  await until(() => env.engine.status().state === 'serverTooOld', 'serverTooOld');
  assert.equal(env.engine.status().message, 'Update your PrivatePaste server to keep syncing.');
  assert.equal(env.server.count('vault'), 0);
  assert.equal(env.server.count('sync'), 0);
  // It keeps checking, so an upgraded server is picked up.
  env.server.infoError = null;
  await env.clock.advance(BACKOFF_MS.at(-1));
  await until(() => env.engine.status().state === 'syncing', 'syncing');
});

test('426 is appTooOld', async () => {
  const env = await setup();
  env.server.syncError = new ApiError(426, null, 'Update PrivatePaste to keep syncing.');
  await env.engine.start();
  await until(() => env.engine.status().state === 'appTooOld', 'appTooOld');
  assert.equal(env.engine.status().message, 'Update PrivatePaste to keep syncing.');
  await env.clock.advance(60000);
  assert.equal(env.server.count('sync'), 1);   // stopped for good
  await env.engine.onLocalChange({ text: 'not sent' });
  assert.equal(env.server.uploads.length, 0);
});

test('kind not in server kinds is skipped', async () => {
  const env = await setup();
  env.server.info = { ...env.server.info, kinds: ['text', 'link'] };
  await connected(env);
  await env.engine.onLocalChange({ png: await encodePng(new Uint8Array([1, 2, 3, 4]), 1, 1) });
  assert.equal(env.server.uploads.length, 0);
  await env.engine.onLocalChange({ text: 'still sent' });
  assert.equal(env.server.uploads.length, 1);
});

test('a copied image uploads with its thumbnail and blob', async () => {
  const env = await setup();
  await connected(env);
  const png = await encodePng(Uint8Array.from([255, 0, 0, 128]), 1, 1);
  await env.engine.onLocalChange({ png });
  assert.equal(env.server.uploads.length, 1);
  const upload = env.server.uploads[0];
  assert.equal(upload.meta.kind, 'image');
  assert.deepEqual(await e2e.decryptBlob(keys, upload.blobs[0]), png);
  assert.ok(upload.thumb);
});

test('paused uploads nothing and applies nothing', async () => {
  const env = await setup();
  const socket = await connected(env);
  await env.engine.setPaused(true);
  assert.equal(env.engine.status().state, 'paused');
  assert.equal(env.engine.status().paused, true);
  await settle();
  assert.equal(socket.readyState, 3);
  await env.engine.onLocalChange({ text: 'secret' });
  socket.receive({ type: 'clip.created', clip: await clip({ id: 'p', seq: 1, at: env.clock.now() + 1000 }), live: true });
  await settle(30);
  assert.equal(env.server.uploads.length, 0);
  assert.deepEqual(env.clipboard.written, []);
  await env.clock.advance(60000);
  assert.equal(env.ws.sockets.length, 1);

  // Resuming reconnects; what was copied elsewhere meanwhile goes to history only.
  env.server.syncPages.push({ clips: [await clip({ id: 'q', seq: 2, at: env.clock.now() + 2000 })], deletedIds: [], latestSeq: 2, hasMore: false });
  await env.engine.setPaused(false);
  await until(() => env.engine.status().state === 'syncing' && env.ws.open().length === 1, 'syncing');
  assert.deepEqual(env.clipboard.written, []);
  assert.ok((await env.store.getCache()).some(c => c.id === 'q'));
  await env.engine.onLocalChange({ text: 'after resuming' });
  assert.equal(env.server.uploads.length, 1);
});

test('oversized text is skipped once', async () => {
  const env = await setup();
  env.server.info = { ...env.server.info, limits: { textKb: 1, imageMb: 20, fileMb: 25 } };
  await connected(env);
  const seen = env.statuses.length;
  await env.engine.onLocalChange({ text: 'x'.repeat(2000) });
  assert.equal(env.engine.status().state, 'syncing');
  assert.equal(env.engine.status().message, 'Clip too large to sync (text over 1 KB).');
  assert.equal(env.statuses.length, seen + 1);
  await env.engine.onLocalChange({ text: 'x'.repeat(2000) });
  assert.equal(env.statuses.length, seen + 1);
  assert.equal(env.server.uploads.length, 0);
});

test('restart keeps the vault', async () => {
  const first = await setup();
  await connected(first);
  first.engine.stop();
  const clock = fakeClock();
  const ws = fakeSockets();
  const engine = createEngine({ api: first.server.api, store: first.store, clipboard: fakeClipboard(), clock, WebSocket: ws.FakeWebSocket });
  await engine.start();
  await until(() => engine.status().state === 'syncing', 'syncing');
  const vault = await first.store.getVault();
  assert.equal(vault.encKey.extractable, false);
  assert.equal(vault.raw, undefined);
});

test('sign in, then unlock with the passphrase', async () => {
  const wrapCase = vectors.passphraseWrap[0];
  const env = await setup({ signedIn: false, wrap: wrapCase.wrap });
  await env.engine.start();
  assert.equal(env.engine.status().state, 'signedOut');
  await assert.rejects(() => env.engine.signIn(SERVER, 'owner', 'wrong', 'Edge'), /Wrong name or password\./);
  const s = await env.engine.signIn(`${SERVER}/`, 'owner', 'dev', '');
  assert.equal(s.state, 'locked');
  assert.equal(s.deviceName, 'Edge');
  assert.deepEqual(env.server.calls.find(c => c.name === 'login').args, ['owner', 'Edge']);
  assert.deepEqual(await env.store.getSession(), { serverUrl: SERVER, token: 'tok-new', deviceId: OWN, deviceName: 'Edge' });

  await assert.rejects(() => env.engine.unlock('not the passphrase'), { message: MESSAGES.wrongPassphrase });
  await env.engine.unlock(wrapCase.passphrase);
  await until(() => env.engine.status().state === 'syncing', 'syncing');
  const vault = await env.store.getVault();
  assert.equal(vault.keyId, wrapCase.wrap.keyId);
  assert.equal(vault.encKey.extractable, false);
});

test('plain http is refused except for this computer, before anything is sent', async () => {
  const env = await setup({ signedIn: false, wrap: vectors.passphraseWrap[0].wrap });
  await env.engine.start();
  for (const url of ['http://192.168.1.20:8080', 'HTTP://paste.example.com', ' http://10.0.0.2/ ']) {
    await assert.rejects(() => env.engine.signIn(url, 'owner', 'dev', 'Edge'),
      { message: 'The server address must use https:// (http:// is only allowed for localhost).' });
  }
  await assert.rejects(() => env.engine.signIn('paste.example.com', 'owner', 'dev', 'Edge'),
    { message: 'Enter a full server address starting with https://' });
  assert.deepEqual(env.server.calls, []);
  for (const url of ['http://127.0.0.1:5202', 'http://[::1]:5202', 'https://paste.example.com']) {
    assert.equal((await env.engine.signIn(url, 'owner', 'dev', 'Edge')).state, 'locked');
  }
});

test('without a vault, someone other than the owner is told to ask the owner', async () => {
  const env = await setup({ signedIn: false, wrap: null });
  env.server.isOwner = false;
  await env.engine.start();
  const s = await env.engine.signIn(SERVER, 'guest', 'dev', 'Edge');
  assert.equal(s.state, 'noVault');
  assert.equal(s.message, 'Ask the owner to set up encryption.');
  const again = createEngine({ api: env.server.api, store: env.store, clipboard: env.clipboard, clock: env.clock, WebSocket: env.ws.FakeWebSocket });
  await again.start();
  assert.deepEqual([again.status().state, again.status().message], ['noVault', 'Ask the owner to set up encryption.']);
});

test('sign-out revokes the token and deletes everything', async () => {
  const env = await setup();
  await connected(env);
  await env.engine.onLocalChange({ text: 'hello' });
  await env.engine.signOut();
  assert.equal(env.server.count('logout'), 1);
  assert.equal(env.engine.status().state, 'signedOut');
  assert.equal(await env.store.getSession(), null);
  assert.equal(await env.store.getVault(), null);
  assert.deepEqual(await env.store.getCache(), []);
});

test('without a socket it polls /api/sync and applies new clips', async () => {
  const env = await setup({ state: { lastAppliedAt: 0 } });
  env.ws.control.refuse = true;
  await env.engine.start();
  await until(() => env.engine.status().state === 'syncing', 'syncing');
  const at = env.clock.now() + 1000;
  env.server.syncPages.push({ clips: [await clip({ id: 'n', seq: 1, at, text: 'polled' })], deletedIds: [], latestSeq: 1, hasMore: false });
  await env.clock.advance(POLL_MS);
  await until(() => env.clipboard.written.length === 1, 'the polled clip');
  assert.deepEqual(env.clipboard.written, ['polled']);
  // A pin change of that clip later is not a copy.
  env.server.syncPages.push({ clips: [await clip({ id: 'n', seq: 2, at, text: 'polled', extra: { pinnedAt: iso(1) } })], deletedIds: [], latestSeq: 2, hasMore: false });
  await env.clock.advance(POLL_MS);
  assert.equal(env.clipboard.written.length, 1);
});

test('the socket pings every 20 s and reconnects without a pong', async () => {
  const env = await setup();
  const socket = await connected(env);
  await env.clock.advance(20000);
  assert.deepEqual(socket.sent, [{ type: 'ping' }]);
  socket.receive({ type: 'pong' });
  await env.clock.advance(20000);
  assert.equal(socket.sent.length, 2);
  await env.clock.advance(10000);
  await until(() => socket.readyState === 3, 'the close');
});

test('history decrypts rows, filters and pages through older clips', async () => {
  const env = await setup();
  const socket = await connected(env);
  await live(socket, await clip({ id: 'a', seq: 1, at: 1000, text: 'Alpha beta' }), false);
  await live(socket, await clip({ id: 'f', seq: 2, at: 2000, kind: 'file', payload: { files: [{ name: 'Report.pdf', size: 10 }] }, extra: { blobCount: 1 } }), false);
  await live(socket, await clip({ id: 'p', seq: 3, at: 3000, text: 'pinned', extra: { pinnedAt: iso(5000) } }), false);

  const all = await env.engine.history();
  assert.deepEqual(all.rows.map(r => r.id), ['p', 'f', 'a']);
  assert.equal(all.rows[2].text, 'Alpha beta');
  assert.equal(all.rows[2].sourceDevicePlatform, 'android');
  assert.deepEqual(all.rows[1].files, [{ name: 'Report.pdf', size: 10 }]);
  assert.deepEqual((await env.engine.history({ query: 'BETA' })).rows.map(r => r.id), ['a']);
  assert.deepEqual((await env.engine.history({ query: 'report' })).rows.map(r => r.id), ['f']);
  assert.deepEqual((await env.engine.history({ pinned: true })).rows.map(r => r.id), ['p']);

  env.server.pages.push({ items: [await clip({ id: 'a', seq: 1, at: 1000 }), await clip({ id: 'old', seq: 0, at: 500, text: 'older beta' })], nextCursor: 'c2' });
  const older = await env.engine.history({ cursor: all.nextCursor, query: 'beta' });
  assert.deepEqual(older.rows.map(r => r.id), ['old']);
  assert.equal(older.nextCursor, 'server:c2');
  assert.equal(await env.engine.copy('old'), true);
  assert.deepEqual(env.clipboard.written, ['older beta']);
});

test('history rows of images carry the decrypted thumbnail', async () => {
  const env = await setup();
  const socket = await connected(env);
  const thumb = await encodePng(Uint8Array.from([9, 9, 9, 255]), 1, 1);
  env.server.thumbs.set('i', await e2e.sealSmall(keys, 'thumb', thumb));
  await live(socket, await clip({ id: 'i', seq: 1, at: 1000, kind: 'image', payload: { width: 1, height: 1 }, extra: { hasThumb: true, blobCount: 1 } }), false);
  const [row] = (await env.engine.history()).rows;
  assert.equal(row.kind, 'image');
  assert.deepEqual(row.thumbPng, thumb);
  await env.engine.history();
  assert.equal(env.server.count('thumb'), 1);
});

test('historyVersion changes with the cache and only then', async () => {
  const env = await setup();
  const socket = await connected(env);
  const v0 = env.engine.historyVersion();
  await env.engine.history();
  assert.equal(env.engine.historyVersion(), v0, 'reading the history is no change');

  await live(socket, await clip({ id: 'a', seq: 1, at: 1000, text: 'one' }), false);
  const v1 = env.engine.historyVersion();
  assert.notEqual(v1, v0);
  await env.clock.advance(5000);
  assert.equal(env.engine.historyVersion(), v1, 'nothing arrived');

  // The same clip again (an old seq) changes nothing; a pin (new seq) does; a deletion does.
  await live(socket, await clip({ id: 'a', seq: 1, at: 1000, text: 'one' }), false);
  assert.equal(env.engine.historyVersion(), v1);
  await live(socket, await clip({ id: 'a', seq: 2, at: 1000, text: 'one', extra: { pinnedAt: iso(3000) } }), false);
  const v2 = env.engine.historyVersion();
  assert.notEqual(v2, v1);
  socket.receive({ type: 'clip.deleted', id: 'a', seq: 3 });
  await until(() => env.engine.historyVersion() !== v2, 'the deletion');

  const v3 = env.engine.historyVersion();
  await env.engine.signOut();
  assert.notEqual(env.engine.historyVersion(), v3, 'sign-out empties the cache');
});

test('a thumbnail that failed is asked for again only when the popup opens', async () => {
  const env = await setup();
  const socket = await connected(env);
  await live(socket, await clip({ id: 'i', seq: 1, at: 1000, kind: 'image', payload: { width: 1, height: 1 }, extra: { hasThumb: true, blobCount: 1 } }), false);
  // No thumbnail on the server: every ask fails.
  assert.equal((await env.engine.history()).rows[0].thumbPng, undefined);
  await env.engine.history();
  await env.engine.history();
  assert.equal(env.server.count('thumb'), 1);
  await env.engine.history({ retryThumbs: true });
  assert.equal(env.server.count('thumb'), 2);

  const thumb = await encodePng(Uint8Array.from([9, 9, 9, 255]), 1, 1);
  env.server.thumbs.set('i', await e2e.sealSmall(keys, 'thumb', thumb));
  assert.equal((await env.engine.history()).rows[0].thumbPng, undefined, 'not before the next open');
  assert.deepEqual((await env.engine.history({ retryThumbs: true })).rows[0].thumbPng, thumb);
});

test('copy writes text as its own write, and leaves images to the popup', async () => {
  const env = await setup();
  const socket = await connected(env);
  await live(socket, await clip({ id: 't', seq: 1, at: 1000, text: 'again' }), false);
  const png = await encodePng(Uint8Array.from([1, 2, 3, 200]), 1, 1);
  env.server.blobs.set('i/0', await e2e.encryptBlob(keys, png));
  await live(socket, await clip({ id: 'i', seq: 2, at: 2000, kind: 'image', payload: { width: 1, height: 1 }, extra: { blobCount: 1 } }), false);

  assert.equal(await env.engine.copy('t'), true);
  assert.deepEqual(env.clipboard.written, ['again']);
  await env.engine.onLocalChange({ text: 'again' });
  // An image comes back decrypted for the popup to write, fetched and decrypted once.
  assert.deepEqual(await env.engine.copy('i'), { png });
  assert.equal(env.server.count('blob'), 1);
  assert.deepEqual(env.clipboard.images, []);
  // The popup writes it; the poll reading it back is not uploaded.
  await env.engine.onLocalChange({ png });
  assert.equal(env.server.uploads.length, 0);
});

test('download gives the decrypted file and its safe name', async () => {
  const env = await setup();
  const socket = await connected(env);
  const bytes = new TextEncoder().encode('file body');
  env.server.blobs.set('f/1', await e2e.encryptBlob(keys, bytes));
  await live(socket, await clip({
    id: 'f', seq: 1, at: 1000, kind: 'file',
    payload: { files: [{ name: 'a.txt', size: 1 }, { name: '..\\evil<1>.txt', size: 9, mime: 'text/plain' }] }, extra: { blobCount: 2 },
  }), false);
  const file = await env.engine.download('f', 1);
  assert.deepEqual(file.bytes, bytes);
  assert.equal(file.name, 'evil1.txt');
  assert.equal(file.mime, 'text/plain');
  assert.equal(await env.engine.copy('f'), false);
  await assert.rejects(() => env.engine.download('f', 2));
});

// ---- the rules that matter most, on both links: the socket, and polling /api/sync (when the socket can't open) ----

/** Connected with an open socket, or (mode 'poll') with the socket refused, so the engine polls. */
async function connectedIn(env, mode) {
  if (mode === 'socket') return connected(env);
  env.ws.control.refuse = true;
  await env.engine.start();
  await until(() => env.engine.status().state === 'syncing' && env.engine.status().link === 'polling', 'polling');
  return null;
}

/** Clips from another device reaching the engine: live messages on the socket, or the next poll's page. */
async function deliver(env, mode, socket, wires) {
  if (mode === 'socket') {
    for (const wire of wires) await live(socket, wire);
    return;
  }
  env.server.syncPages.push({ clips: wires, deletedIds: [], latestSeq: Math.max(...wires.map(w => w.seq)), hasMore: false });
  await env.clock.advance(POLL_MS);
  await settle(30);
}

/** The user copies text in another app and the poller reports it. */
async function userCopies(env, text) {
  env.clipboard.copyHere(text);
  await env.engine.onLocalChange({ text });
}

for (const mode of ['socket', 'poll']) {
  test(`own write is not uploaded (${mode})`, async () => {
    const env = await setup();
    const socket = await connectedIn(env, mode);
    const wire = await clip({ id: 'p1', seq: 1, at: env.clock.now() + 1000, text: 'from the\nphone' });
    await deliver(env, mode, socket, [wire]);
    assert.deepEqual(env.clipboard.written, ['from the\nphone']);
    // The poller reads the write back (Windows may hand it back with CRLF; the hash is the same).
    await env.engine.onLocalChange({ text: 'from the\r\nphone' });
    await env.engine.onLocalChange({ text: 'from the\nphone' });
    assert.equal(env.server.uploads.length, 0);
    // Not re-applied either when the same clip comes again.
    await deliver(env, mode, socket, [wire]);
    assert.equal(env.clipboard.written.length, 1);
  });

  test(`older live clip does not overwrite a newer local copy (${mode})`, async () => {
    const env = await setup();
    const socket = await connectedIn(env, mode);
    const T = env.clock.now();
    await userCopies(env, 'copied here');
    assert.equal(env.server.stored[0].lastCopiedAt, iso(T));
    await deliver(env, mode, socket, [await clip({ id: 'p1', seq: 200, at: T - 1000, text: 'older from the phone' })]);
    assert.deepEqual(env.clipboard.written, []);
    assert.ok((await env.store.getCache()).some(c => c.id === 'p1'));
    // A newer one still applies.
    await deliver(env, mode, socket, [await clip({ id: 'p2', seq: 201, at: env.clock.now() + 1000, text: 'newer from the phone' })]);
    assert.deepEqual(env.clipboard.written, ['newer from the phone']);
  });

  test(`own device's clip is not applied (${mode})`, async () => {
    const env = await setup();
    const socket = await connectedIn(env, mode);
    await deliver(env, mode, socket, [await clip({ id: 'o1', seq: 1, at: env.clock.now() + 1000, device: OWN })]);
    assert.deepEqual(env.clipboard.written, []);
    assert.ok((await env.store.getCache()).some(c => c.id === 'o1'));
  });

  test(`a copy the poller has not read yet is not overwritten (${mode})`, async () => {
    const env = await setup();
    const socket = await connectedIn(env, mode);
    await userCopies(env, 'known');
    env.clipboard.copyHere('just copied');   // not polled yet
    await deliver(env, mode, socket, [await clip({ id: 'p1', seq: 1, at: env.clock.now() + 1000, text: 'incoming' })]);
    assert.deepEqual(env.clipboard.written, []);
    assert.deepEqual(env.clipboard.current, { text: 'just copied' });
    assert.ok((await env.store.getCache()).some(c => c.id === 'p1'));
    // Taken as the copy it is: uploaded now, and not again when the poller reports it.
    assert.equal(env.server.uploads.length, 2);
    assert.deepEqual(await e2e.openPayload(keys, env.server.uploads[1].meta.payload), { text: 'just copied' });
    await env.engine.onLocalChange({ text: 'just copied' });
    assert.equal(env.server.uploads.length, 2);
  });

  test(`a copy waiting to be handled is not overwritten (${mode})`, async () => {
    const env = await setup();
    const socket = await connectedIn(env, mode);
    const wire = await clip({ id: 'p1', seq: 1, at: env.clock.now() + 1000, text: 'incoming' });
    let pending;
    if (mode === 'socket') {
      socket.receive({ type: 'clip.created', clip: wire, live: true });
      pending = env.engine.onLocalChange({ text: 'copied meanwhile' });
    } else {
      let release;
      env.server.syncHold = new Promise(r => { release = r; });
      env.server.syncPages.push({ clips: [wire], deletedIds: [], latestSeq: 1, hasMore: false });
      await env.clock.advance(POLL_MS);   // the poll is waiting for /api/sync
      pending = env.engine.onLocalChange({ text: 'copied meanwhile' });
      env.server.syncHold = null;
      release();
    }
    await pending;
    await settle(30);
    assert.deepEqual(env.clipboard.written, []);
    assert.equal(env.server.uploads.length, 1);
    assert.ok((await env.store.getCache()).some(c => c.id === 'p1'));
  });
}

test('the clipboard as it was at start is the baseline, not a new copy', async () => {
  const env = await setup();
  env.clipboard.copyHere('there before the extension started');
  const socket = await connected(env);
  await live(socket, await clip({ id: 'p1', seq: 1, at: env.clock.now() + 1000, text: 'incoming' }));
  assert.deepEqual(env.clipboard.written, ['incoming']);
  assert.equal(env.server.uploads.length, 0);
});

// ---- copies made while not syncing, and the poller's baseline ----

for (const reported of [true, false]) {
  test(`a copy made while paused is never uploaded (${reported ? 'reported by the poller' : 'never read'})`, async () => {
    const env = await setup();
    await connected(env);
    await userCopies(env, 'before');
    await env.engine.setPaused(true);
    env.clipboard.copyHere('secret');
    if (reported) await env.engine.onLocalChange({ text: 'secret' });
    await env.engine.setPaused(false);
    await until(() => env.engine.status().state === 'syncing' && env.ws.open().length === 1, 'syncing');
    await live(env.ws.open()[0], await clip({ id: 'p1', seq: 1, at: env.clock.now() + 1000, text: 'incoming' }));
    assert.equal(env.server.uploads.length, 1);
    assert.deepEqual(await e2e.openPayload(keys, env.server.uploads[0].meta.payload), { text: 'before' });
    // Newest wins, as after the first catch-up: the incoming clip goes on the clipboard.
    assert.deepEqual(env.clipboard.written, ['incoming']);
  });
}

test('a copy made while locked is never uploaded after unlocking', async () => {
  const wrapCase = vectors.passphraseWrap[0];
  const env = await setup({ wrap: wrapCase.wrap });
  const socket = await connected(env);
  await userCopies(env, 'before');
  socket.receive({ type: 'vault.reset' });
  await until(() => env.engine.status().state === 'locked', 'locked');
  await userCopies(env, 'secret');
  await env.engine.unlock(wrapCase.passphrase);
  await until(() => env.engine.status().state === 'syncing' && env.ws.open().length === 1, 'syncing');
  await live(env.ws.open()[0], await clip({ id: 'p1', seq: 1, at: env.clock.now() + 1000, text: 'incoming' }));
  assert.equal(env.server.uploads.length, 1);
  assert.deepEqual(env.clipboard.written, ['incoming']);
});

test('the clipboard is wanted only while syncing or offline', async () => {
  const env = await setup();
  assert.equal(env.engine.wantsClipboard(), false, 'before start');
  await connected(env);
  assert.equal(env.engine.wantsClipboard(), true);
  await env.engine.setPaused(true);
  assert.equal(env.engine.wantsClipboard(), false, 'paused');
  await env.engine.setPaused(false);
  assert.equal(env.engine.wantsClipboard(), true);
  await until(() => env.ws.open().length === 1, 'the socket');
  env.ws.open()[0].receive({ type: 'vault.reset' });
  await until(() => env.engine.status().state === 'locked', 'locked');
  assert.equal(env.engine.wantsClipboard(), false, 'locked');
  await env.engine.signOut();
  assert.equal(env.engine.wantsClipboard(), false, 'signed out');
});

for (const before of ['there before', null]) {
  test(`a copy made before the first poll after start is not overwritten (${before ? 'text' : 'empty'} at start)`, async () => {
    const env = await setup();
    if (before) env.clipboard.copyHere(before);
    const socket = await connected(env);
    await env.engine.setBaseline(before ? { text: before } : {});   // the poller's first read
    env.clipboard.copyHere('copied right after start');
    await live(socket, await clip({ id: 'p1', seq: 1, at: env.clock.now() + 1000, text: 'incoming' }));
    assert.deepEqual(env.clipboard.written, []);
    assert.equal(env.server.uploads.length, 1);
    assert.deepEqual(await e2e.openPayload(keys, env.server.uploads[0].meta.payload), { text: 'copied right after start' });
  });
}

test('a baseline read after the engine already knows the clipboard is a copy when it differs', async () => {
  const env = await setup();
  const socket = await connected(env);
  await live(socket, await clip({ id: 'p1', seq: 1, at: env.clock.now() + 1000, text: 'incoming' }));
  assert.deepEqual(env.clipboard.written, ['incoming']);
  await env.engine.setBaseline({ text: 'incoming' });
  assert.equal(env.server.uploads.length, 0);
  env.clipboard.copyHere('copied before the poll');
  await env.engine.setBaseline({ text: 'copied before the poll' });
  assert.equal(env.server.uploads.length, 1);
});

test('catch-up applies only the newest (poll)', async () => {
  const env = await setup();
  await connectedIn(env, 'poll');
  const at = env.clock.now();
  await deliver(env, 'poll', null, [
    await clip({ id: 'a', seq: 1, at: at + 1000, text: 'first' }),
    await clip({ id: 'c', seq: 3, at: at + 3000, text: 'third' }),
    await clip({ id: 'b', seq: 2, at: at + 2000, text: 'second' }),
  ]);
  assert.deepEqual(env.clipboard.written, ['third']);
  // Older than what was applied: history only.
  await deliver(env, 'poll', null, [await clip({ id: 'd', seq: 4, at: at + 2500, text: 'late' })]);
  assert.deepEqual(env.clipboard.written, ['third']);
});

// ---- the socket's credentials (ruling R-X8) ----

test('the socket offers the protocol version and the token as subprotocols', async () => {
  const env = await setup();
  const socket = await connected(env);
  assert.equal(socket.url, 'ws://localhost:5202/ws');
  assert.deepEqual(socket.protocols, ['privatepaste.v2', 'privatepaste.auth.tok']);
  assert.equal(env.engine.status().link, 'socket');
  await env.engine.signOut();
  assert.equal(env.engine.status().link, null);
});

test('after a sign-in the socket offers the new token', async () => {
  const env = await setup({ signedIn: false, wrap: vectors.passphraseWrap[0].wrap });
  await env.engine.start();
  await env.engine.signIn(SERVER, 'owner', 'dev', 'Edge');
  await env.engine.unlock(vectors.passphraseWrap[0].passphrase);
  await until(() => env.ws.open().length === 1, 'the socket');
  assert.deepEqual(env.ws.open()[0].protocols, ['privatepaste.v2', 'privatepaste.auth.tok-new']);
});

test('the status says when it polls instead of using the socket', async () => {
  const env = await setup();
  await connectedIn(env, 'poll');
  assert.equal(env.engine.status().link, 'polling');
  assert.ok(env.statuses.some(s => s.link === 'polling'));
});

// ---- more client rules ----

test('409 Encryption was reset on a queued upload relocks and drops the queue', async () => {
  const env = await setup();
  await env.store.enqueue({ copiedAt: Date.now(), kind: 'text', sealed: await e2e.encryptBlob(keys, new TextEncoder().encode('queued')) });
  await env.store.enqueue({ copiedAt: Date.now(), kind: 'text', sealed: await e2e.encryptBlob(keys, new TextEncoder().encode('queued too')) });
  env.server.uploadErrors.push(new ApiError(409, 'Encryption was reset', MESSAGES.reset));
  await env.engine.start();
  await until(() => env.engine.status().state === 'locked', 'locked');
  assert.equal(env.server.uploads.length, 1);
  assert.equal(env.server.uploads[0].meta.backfill, true);
  assert.equal(env.engine.status().message, MESSAGES.reset);
  assert.deepEqual(await env.store.queue(), []);
  assert.equal(await env.store.getVault(), null);
});

test('an older seq of a clip arriving late does not replace the newer one', async () => {
  const env = await setup();
  const socket = await connected(env);
  const at = env.clock.now() - 1000;
  await live(socket, await clip({ id: 'x', seq: 5, at, extra: { pinnedAt: iso(at) } }), false);
  await live(socket, await clip({ id: 'x', seq: 4, at }), false);
  const [cached] = await env.store.getCache();
  assert.equal(cached.seq, 5);
  assert.ok(cached.pinnedAt);
  // Nor does a deleted clip come back.
  socket.receive({ type: 'clip.deleted', id: 'x', seq: 6 });
  await settle(30);
  await live(socket, await clip({ id: 'x', seq: 5, at }), false);
  assert.deepEqual(await env.store.getCache(), []);
});

test('a vault gone at connect wipes the key and asks the owner to set encryption up', async () => {
  const env = await setup({ wrap: null });
  await env.store.putClips([await clip({ id: 'x', seq: 1, at: 1000 })]);
  await env.engine.start();
  await until(() => env.engine.status().state === 'noVault', 'noVault');
  assert.equal(env.engine.status().message, MESSAGES.ownerSetUp);
  assert.equal(await env.store.getVault(), null);
  assert.deepEqual(await env.store.getCache(), []);
});

test('an upload answered "Set up encryption first." wipes the key too', async () => {
  const env = await setup();
  await connected(env);
  env.server.uploadErrors.push(new ApiError(409, 'Conflict', 'Set up encryption first.'));
  await env.engine.onLocalChange({ text: 'x' });
  await until(() => env.engine.status().state === 'noVault', 'noVault');
  assert.equal(await env.store.getVault(), null);
});

test('reconnects back off 1, 2, 5, 10, then 30 s', async () => {
  const env = await setup();
  env.server.infoError = new TypeError('fetch failed');
  await env.engine.start();
  await until(() => env.server.count('serverInfo') === 1, 'the first try');
  for (const [i, delay] of [1000, 2000, 5000, 10000, 30000, 30000].entries()) {
    await env.clock.advance(delay - 1);
    assert.equal(env.server.count('serverInfo'), i + 1, `not before ${delay} ms`);
    await env.clock.advance(1);
    await until(() => env.server.count('serverInfo') === i + 2, `the try after ${delay} ms`);
  }
  assert.equal(env.engine.status().state, 'offline');
});

test('a second sign-in revokes the previous token', async () => {
  const env = await setup();
  await connected(env);
  await env.engine.signIn(SERVER, 'owner', 'dev', 'Edge');
  assert.equal(env.server.count('logout'), 1);
  assert.equal((await env.store.getSession()).token, 'tok-new');
});

test('a queued copy the server rejects is dropped with a plain notice', async () => {
  const env = await setup();
  await env.store.enqueue({ copiedAt: Date.now(), kind: 'text', sealed: await e2e.encryptBlob(keys, new TextEncoder().encode('q')) });
  env.server.uploadErrors.push(new ApiError(400, 'Bad Request', null));
  await connected(env);
  assert.deepEqual(await env.store.queue(), []);
  assert.ok(env.statuses.some(st => st.message === MESSAGES.queuedRejected));
});
