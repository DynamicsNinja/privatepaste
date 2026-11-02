import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createPoller } from '../src/core/poller.js';

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
