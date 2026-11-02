import { test } from 'node:test';
import assert from 'node:assert/strict';
import { historyWatch } from '../src/ui/history-watch.js';

test('the refresh asks only for the version while nothing changes', async () => {
  let version = 'boot:1';
  const asked = [];
  const ask = async message => { asked.push(message.type); return version; };
  const watch = historyWatch(ask);
  assert.equal(await watch.changed(), true, 'the first look');
  assert.equal(await watch.changed(), false);
  assert.equal(await watch.changed(), false);
  version = 'boot:2';
  assert.equal(await watch.changed(), true);
  assert.equal(await watch.changed(), false);
  version = 'restarted:2';   // an offscreen document that restarted counts as a change
  assert.equal(await watch.changed(), true);
  assert.deepEqual(new Set(asked), new Set(['engine.historyVersion']));
});
