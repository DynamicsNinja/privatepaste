import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createApi, ApiError, isTransient } from '../src/core/api.js';

function fakeFetch(respond) {
  const calls = [];
  const fetch = async (url, init = {}) => {
    const call = { url: String(url), method: init.method ?? 'GET', headers: new Headers(init.headers), body: init.body };
    calls.push(call);
    return respond(call);
  };
  return { fetch, calls };
}

const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
const problem = (status, title, detail) =>
  new Response(JSON.stringify({ status, title, detail }), { status, headers: { 'content-type': 'application/problem+json' } });

test('every call sends the protocol header and the bearer token', async () => {
  const { fetch, calls } = fakeFetch(() => json({}));
  const api = createApi({ serverUrl: 'http://localhost:5202/', token: 'tok', fetch });
  await api.serverInfo();
  await api.me();
  await api.sync(7);
  await api.clips({ cursor: 'abc', limit: 20, pinned: true });
  await api.pin('id1');
  await api.unpin('id1');
  assert.deepEqual(calls.map(c => `${c.method} ${c.url}`), [
    'GET http://localhost:5202/api/server-info',
    'GET http://localhost:5202/api/me',
    'GET http://localhost:5202/api/sync?afterSeq=7',
    'GET http://localhost:5202/api/clips?cursor=abc&limit=20&pinned=true',
    'POST http://localhost:5202/api/clips/id1/pin',
    'DELETE http://localhost:5202/api/clips/id1/pin',
  ]);
  for (const c of calls) {
    assert.equal(c.headers.get('X-PrivatePaste-Protocol'), '2');
    assert.equal(c.headers.get('Authorization'), 'Bearer tok');
  }
});

test('login sends platform extension and returns the device token', async () => {
  const { fetch, calls } = fakeFetch(() => json({ deviceId: 'd1', token: 't1' }));
  const api = createApi({ serverUrl: 'http://localhost:5202', fetch });
  assert.deepEqual(await api.login('owner', 'dev', 'Edge'), { deviceId: 'd1', token: 't1' });
  assert.equal(calls[0].method, 'POST');
  assert.equal(calls[0].url, 'http://localhost:5202/api/auth/login');
  assert.deepEqual(JSON.parse(calls[0].body), { username: 'owner', password: 'dev', deviceName: 'Edge', platform: 'extension' });
  assert.equal(calls[0].headers.get('Authorization'), null);
  assert.equal(calls[0].headers.get('X-PrivatePaste-Protocol'), '2');
});

test('failures throw ApiError with status, title and detail', async () => {
  const { fetch } = fakeFetch(() => problem(401, 'Unauthorized', 'Wrong name or password.'));
  const api = createApi({ serverUrl: 'http://localhost:5202', fetch });
  const e = await api.login('a', 'b', 'c').catch(e => e);
  assert.ok(e instanceof ApiError);
  assert.equal(e.status, 401);
  assert.equal(e.title, 'Unauthorized');
  assert.equal(e.detail, 'Wrong name or password.');
  assert.equal(e.message, 'Wrong name or password.');

  const plain = fakeFetch(() => new Response('nope', { status: 503 }));
  const e2 = await createApi({ serverUrl: 'http://x', token: 't', fetch: plain.fetch }).me().catch(e => e);
  assert.equal(e2.status, 503);
  assert.equal(e2.title, null);
  assert.ok(isTransient(e2));
  assert.ok(isTransient(new TypeError('fetch failed')));
  assert.ok(!isTransient(new ApiError(410, 'Cleared', null)));
  assert.ok(isTransient(new ApiError(429, null, null)));
});

test('vault() is null when encryption is not set up (404)', async () => {
  const { fetch } = fakeFetch(() => problem(404, 'Not Found', null));
  assert.equal(await createApi({ serverUrl: 'http://x', token: 't', fetch }).vault(), null);
});

test('upload sends multipart meta, thumb and numbered blobs', async () => {
  const { fetch, calls } = fakeFetch(() => json({ id: 'c1', seq: 3 }));
  const api = createApi({ serverUrl: 'http://x', token: 't', fetch });
  const meta = { kind: 'image', backfill: false };
  assert.deepEqual(await api.upload({ meta, thumb: new Uint8Array([1]), blobs: [new Uint8Array([2, 3])] }), { id: 'c1', seq: 3 });
  const c = calls[0];
  assert.equal(c.method, 'POST');
  assert.equal(c.url, 'http://x/api/clips');
  assert.ok(c.body instanceof FormData);
  assert.deepEqual(JSON.parse(await c.body.get('meta').text()), meta);
  assert.deepEqual(new Uint8Array(await c.body.get('thumb').arrayBuffer()), new Uint8Array([1]));
  assert.deepEqual(new Uint8Array(await c.body.get('blob0').arrayBuffer()), new Uint8Array([2, 3]));
  assert.equal(c.body.get('blob1'), null);
});

test('blob and thumb come back as bytes; a missing thumbnail is null', async () => {
  const { fetch, calls } = fakeFetch(c => (c.url.endsWith('/thumb') ? problem(404, 'Not Found') : new Response(new Uint8Array([9, 8]))));
  const api = createApi({ serverUrl: 'http://x', token: 't', fetch });
  assert.deepEqual(await api.blob('c1', 2), new Uint8Array([9, 8]));
  assert.equal(await api.thumb('c1'), null);
  assert.equal(calls[0].url, 'http://x/api/clips/c1/blobs/2');
});

test('remove and logout expect no content', async () => {
  const { fetch, calls } = fakeFetch(() => new Response(null, { status: 204 }));
  const api = createApi({ serverUrl: 'http://x', token: 't', fetch });
  await api.remove('c1');
  await api.logout();
  assert.deepEqual(calls.map(c => `${c.method} ${c.url}`), ['DELETE http://x/api/clips/c1', 'POST http://x/api/auth/logout']);
});

test('the WebSocket URL follows the server scheme', () => {
  assert.equal(createApi({ serverUrl: 'http://localhost:5202', fetch }).socketUrl(), 'ws://localhost:5202/ws');
  assert.equal(createApi({ serverUrl: 'https://paste.example/base/', fetch }).socketUrl(), 'wss://paste.example/base/ws');
});

test('ids are path-escaped', async () => {
  const { fetch, calls } = fakeFetch(() => json({}));
  await createApi({ serverUrl: 'http://x', token: 't', fetch }).pin('a/b?c');
  assert.equal(calls[0].url, 'http://x/api/clips/a%2Fb%3Fc/pin');
});
