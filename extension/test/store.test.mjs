import './support/register.mjs';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const { openStore, memoryBackend, CACHE_CAPACITY } = await import('../src/core/store.js');

const clip = (id, seq, at, extra = {}) => ({ id, seq, lastCopiedAt: new Date(at).toISOString(), pinnedAt: null, ...extra });

test('setVault keeps only non-extractable keys and wipes the raw key', async () => {
  const store = openStore(memoryBackend());
  const raw = Uint8Array.from({ length: 32 }, (_, i) => i);
  const unwrapped = { keyId: 'ignored', raw };
  await store.setVault(unwrapped);
  const vault = await store.getVault();
  assert.equal(vault.keyId, '13dee80fba1d5400');
  assert.equal(vault.encKey.extractable, false);
  assert.equal(vault.hashKey.extractable, false);
  assert.equal(vault.raw, undefined);
  assert.ok(raw.every(b => b === 0));
});

test('state merges and has defaults', async () => {
  const store = openStore(memoryBackend());
  assert.deepEqual(await store.getState(), { highestSeq: 0, lastAppliedAt: null, lastLocalAt: null, paused: false, serverInfo: null });
  await store.setState({ highestSeq: 5 });
  await store.setState({ paused: true });
  const s = await store.getState();
  assert.equal(s.highestSeq, 5);
  assert.equal(s.paused, true);
});

test('the cache keeps the newest copy of each clip, newest first', async () => {
  const backend = memoryBackend();
  const store = openStore(backend);
  assert.equal(await store.putClips([clip('a', 1, 1000), clip('b', 2, 2000)]), true);
  assert.equal(await store.putClips([clip('a', 1, 1000)]), false);
  await store.putClips([clip('a', 3, 3000)]);
  assert.deepEqual((await store.getCache()).map(c => c.id), ['a', 'b']);
  await store.removeClip('a');
  assert.deepEqual((await openStore(backend).getCache()).map(c => c.id), ['b']);
});

test('the cache holds 500 clips plus older pinned ones', async () => {
  const store = openStore(memoryBackend());
  const clips = Array.from({ length: CACHE_CAPACITY + 10 }, (_, i) => clip(`c${i}`, i + 1, i * 1000, i === 0 ? { pinnedAt: 'x' } : {}));
  await store.putClips(clips);
  const cached = await store.getCache();
  assert.equal(cached.length, CACHE_CAPACITY + 1);
  assert.equal(cached.at(-1).id, 'c0');
});

test('the queue keeps its order across reopening; clearAll wipes everything', async () => {
  const backend = memoryBackend();
  const store = openStore(backend);
  await store.setSession({ serverUrl: 'http://x', token: 't', deviceId: 'd', deviceName: 'Edge' });
  const a = await store.enqueue({ copiedAt: 1, kind: 'text', sealed: new Uint8Array([1]) });
  await store.enqueue({ copiedAt: 2, kind: 'text', sealed: new Uint8Array([2]) });
  const again = openStore(backend);
  await again.enqueue({ copiedAt: 3, kind: 'text', sealed: new Uint8Array([3]) });
  assert.deepEqual((await again.queue()).map(i => i.copiedAt), [1, 2, 3]);
  await again.dequeue(a.id);
  assert.deepEqual((await again.queue()).map(i => i.copiedAt), [2, 3]);
  await again.clearAll();
  assert.equal(await again.getSession(), null);
  assert.deepEqual(await again.queue(), []);
  assert.deepEqual(await again.getCache(), []);
});
