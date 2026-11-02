import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handleWsAuthMessage, wsAuthRule, SET_WS_AUTH, CLEAR_WS_AUTH, WS_AUTH_RULE_ID } from '../src/ws-auth.js';

function fakeDnr() {
  const updates = [];
  return { updates, updateSessionRules: async u => { updates.push(u); } };
}

test('the rule sets both headers on that one socket URL, for requests from no tab', () => {
  const rule = wsAuthRule('ws://localhost:5202/ws', 'tok');
  assert.equal(rule.id, WS_AUTH_RULE_ID);
  assert.deepEqual(rule.action, {
    type: 'modifyHeaders',
    requestHeaders: [
      { header: 'Authorization', operation: 'set', value: 'Bearer tok' },
      { header: 'X-PrivatePaste-Protocol', operation: 'set', value: '2' },
    ],
  });
  assert.deepEqual(rule.condition, { urlFilter: '|ws://localhost:5202/ws|', resourceTypes: ['websocket'], tabIds: [-1] });
});

test('set replaces the rule; clear removes it', async () => {
  const dnr = fakeDnr();
  assert.deepEqual(await handleWsAuthMessage({ type: SET_WS_AUTH, url: 'wss://paste.example/base/ws', token: 'abc' }, dnr), { ok: true });
  assert.deepEqual(dnr.updates[0].removeRuleIds, [WS_AUTH_RULE_ID]);
  assert.equal(dnr.updates[0].addRules[0].condition.urlFilter, '|wss://paste.example/base/ws|');
  assert.deepEqual(await handleWsAuthMessage({ type: CLEAR_WS_AUTH }, dnr), { ok: true });
  assert.deepEqual(dnr.updates[1], { removeRuleIds: [WS_AUTH_RULE_ID] });
});

test('a URL that is not a socket URL, or a token with spaces, is refused', async () => {
  const dnr = fakeDnr();
  for (const [url, token] of [['https://x/ws', 't'], ['ws://x/other', 't'], ['ws://x*y/ws', 't'], ['ws://x/ws', 'a b'], ['ws://x/ws', '']]) {
    assert.deepEqual(await handleWsAuthMessage({ type: SET_WS_AUTH, url, token }, dnr), { ok: false });
  }
  assert.equal(dnr.updates.length, 0);
});

test('other messages are left alone, and a failed update says so', async () => {
  assert.equal(handleWsAuthMessage({ type: 'test.clipboard.get' }, fakeDnr()), undefined);
  const failing = { updateSessionRules: async () => { throw new Error('no'); } };
  assert.deepEqual(await handleWsAuthMessage({ type: CLEAR_WS_AUTH }, failing), { ok: false });
});
