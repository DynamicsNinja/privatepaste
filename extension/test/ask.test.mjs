import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAsk, EngineError, FAILED, NOT_RUNNING } from '../src/ui/ask.js';

const noSleep = async () => {};
const noReceiver = () => Promise.reject(new Error('Could not establish connection. Receiving end does not exist.'));

test('retries while the offscreen document is not up yet, then gets the answer', async () => {
  let calls = 0;
  const ask = createAsk(async () => (++calls < 3 ? noReceiver() : { ok: true, value: 'status' }), { sleep: noSleep });
  assert.equal(await ask({ type: 'engine.status' }), 'status');
  assert.equal(calls, 3);
});

test('gives up with "not running" after the attempts', async () => {
  let calls = 0;
  const ask = createAsk(() => { calls++; return noReceiver(); }, { sleep: noSleep, attempts: 4 });
  await assert.rejects(ask({ type: 'engine.status' }), e => e instanceof EngineError && e.message === NOT_RUNNING);
  assert.equal(calls, 4);
});

test('never resends on any other failure: a sign-in is sent once', async () => {
  for (const error of ['The message port closed before a response was received.', 'Message length exceeded maximum allowed length.']) {
    let calls = 0;
    const ask = createAsk(() => { calls++; return Promise.reject(new Error(error)); }, { sleep: noSleep });
    await assert.rejects(ask({ type: 'engine.signIn' }), e => e instanceof EngineError && e.message === FAILED);
    assert.equal(calls, 1, error);
  }
});

test('no answer at all is a failure, not a retry', async () => {
  let calls = 0;
  const ask = createAsk(async () => { calls++; return undefined; }, { sleep: noSleep });
  await assert.rejects(ask({ type: 'engine.download' }), e => e.message === FAILED);
  assert.equal(calls, 1);
});

test("the engine's error text comes through", async () => {
  const ask = createAsk(async () => ({ ok: false, error: 'Wrong name or password.' }), { sleep: noSleep });
  await assert.rejects(ask({ type: 'engine.signIn' }), e => e instanceof EngineError && e.message === 'Wrong name or password.');
});
