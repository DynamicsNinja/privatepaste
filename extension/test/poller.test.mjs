import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createPoller, nextDelay, startPollingWith } from '../src/core/poller.js';

// A clipboard whose next read is whatever the test last put there.
function setup() {
  const clip = { value: null };
  const changes = [];
  const poller = createPoller({ read: async () => clip.value, onChange: item => changes.push(item) });
  return { clip, changes, poller };
}

test('the first read sets the baseline and is not reported', async () => {
  const { clip, changes, poller } = setup();
  clip.value = { text: 'before start' };
  await poller.poll();
  assert.deepEqual(changes, []);
});

test('a change is reported once', async () => {
  const { clip, changes, poller } = setup();
  clip.value = { text: 'a' };
  await poller.poll();
  clip.value = { text: 'b' };
  await poller.poll();
  await poller.poll();
  assert.deepEqual(changes, [{ text: 'b' }]);
});

test('an image change is reported, and text with the same image counts as different', async () => {
  const { clip, changes, poller } = setup();
  clip.value = { png: new Uint8Array([1, 2, 3]) };
  await poller.poll();
  clip.value = { png: new Uint8Array([1, 2, 4]) };
  await poller.poll();
  clip.value = { text: 'x', png: new Uint8Array([1, 2, 4]) };
  await poller.poll();
  assert.equal(changes.length, 2);
});

test('a failed read is skipped and keeps the baseline', async () => {
  const { clip, changes, poller } = setup();
  clip.value = { text: 'a' };
  await poller.poll();
  clip.value = null;
  await poller.poll();
  clip.value = { text: 'a' };
  await poller.poll();
  assert.deepEqual(changes, []);
});

test('unreadable content moves the baseline without a report, so copying the same text again counts', async () => {
  const { clip, changes, poller } = setup();
  clip.value = { text: 'a' };
  await poller.poll();
  clip.value = {};
  await poller.poll();
  clip.value = { text: 'a' };
  await poller.poll();
  assert.deepEqual(changes, [{ text: 'a' }]);
});

test('an error in onChange does not stop polling', async () => {
  const clip = { value: { text: 'a' } };
  let calls = 0;
  const poller = createPoller({ read: async () => clip.value, onChange: () => { calls++; throw new Error('boom'); } });
  await poller.poll();
  clip.value = { text: 'b' };
  await poller.poll();
  clip.value = { text: 'c' };
  await poller.poll();
  assert.equal(calls, 2);
});

test('stats count reads and busy time', async () => {
  let now = 0;
  const poller = createPoller({ read: async () => { now += 5; return { text: 'a' }; }, onChange: () => {}, now: () => now });
  await poller.poll();
  await poller.poll();
  assert.equal(poller.stats().reads, 2);
  assert.equal(poller.stats().busyMs, 10);
});

test('busy time counts reading and hashing, not the engine work in onChange', async () => {
  let now = 0;
  let text = 'a';
  const poller = createPoller({ read: async () => { now += 5; return { text }; }, onChange: () => { now += 100; }, now: () => now });
  await poller.poll();
  text = 'b';
  assert.equal(await poller.poll(), 5);
  assert.equal(poller.stats().busyMs, 10);
});

test('a slow read backs the poller off to a tenth of the time, and a fast one returns to the interval', async () => {
  let now = 0;
  let readMs = 3;
  const delays = [];
  let next;
  const handle = startPollingWith({
    read: async () => { now += readMs; return { png: new Uint8Array(4) }; },
    onChange: () => {},
    intervalMs: 1000,
    now: () => now,
    setTimer: (fn, ms) => { delays.push(ms); next = fn; return delays.length; },
    clearTimer: () => {},
  });
  const scheduled = async () => { while (!next) await new Promise(r => setImmediate(r)); };
  const tick = async () => { const fn = next; next = undefined; await fn(); };
  await scheduled();
  readMs = 400;
  await tick();
  readMs = 3;
  await tick();
  handle.stop();
  assert.deepEqual(delays, [1000, 4000, 1000]);
});

test('a read that throws is retried on the next tick', async () => {
  let calls = 0;
  const delays = [];
  let next;
  const handle = startPollingWith({
    read: async () => { calls++; throw new Error('no'); },
    onChange: () => {},
    now: () => 0,
    setTimer: (fn, ms) => { delays.push(ms); next = fn; },
    clearTimer: () => {},
  });
  while (!next) await new Promise(r => setImmediate(r));
  await next();
  handle.stop();
  assert.equal(calls, 2);
  assert.deepEqual(delays, [1000, 1000]);
});

test('nextDelay keeps the interval for fast reads', () => {
  assert.equal(nextDelay(1000, 5), 1000);
  assert.equal(nextDelay(1000, 250), 2500);
});
