import './support/register.mjs';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const rows = await import('../src/ui/rows.js');

const NOW = Date.parse('2026-11-02T12:00:00Z');
const ago = seconds => new Date(NOW - seconds * 1000).toISOString();

test('times read like the web history', () => {
  assert.equal(rows.agoText(ago(5), NOW), 'just now');
  assert.equal(rows.agoText(ago(4 * 60 + 10), NOW), '4 min ago');
  assert.equal(rows.agoText(ago(3 * 3600), NOW), '3 h ago');
  assert.equal(rows.agoText(ago(2 * 86400), NOW), '2 d ago');
  assert.match(rows.agoText(ago(40 * 86400), NOW), /^2026-09-2\d$/);
});

test('row titles: one line of text, image size, file summary, unknown kinds', () => {
  assert.equal(rows.rowTitle({ kind: 'text', text: '  docker compose pull\r\n&& up -d  ' }), 'docker compose pull && up -d');
  assert.equal(rows.rowTitle({ kind: 'text', text: 'x'.repeat(500) }).length, 201);
  assert.equal(rows.rowTitle({ kind: 'link', text: 'https://example.com/a' }), 'https://example.com/a');
  assert.equal(rows.rowTitle({ kind: 'image', width: 1280, height: 720 }), 'Image · 1280 × 720');
  assert.equal(rows.rowTitle({ kind: 'image' }), 'Image');
  assert.equal(rows.rowTitle({ kind: 'file', files: [{ name: 'report.pdf', size: 3 * 1048576 }, { name: 'b.txt', size: 400 * 1024 }] }),
    'report.pdf +1 more · 3.4 MB');
  assert.equal(rows.rowTitle({ kind: 'unknown' }), 'Update PrivatePaste to see this clip');
});

test('meta: person when the server has several, device and time', () => {
  const row = { sourceDeviceName: 'Pixel 9', sourceUserName: 'Ivan', lastCopiedAt: ago(1) };
  assert.equal(rows.metaText(row, NOW), 'Ivan · Pixel 9 · just now');
  assert.equal(rows.metaText({ ...row, sourceUserName: null }, NOW), 'Pixel 9 · just now');
  assert.equal(rows.metaText({ ...row, sourceDeviceName: null, sourceUserName: null }, NOW), 'just now');
});

test('platforms outside the known set show as unknown', () => {
  for (const p of ['windows', 'macos', 'linux', 'android', 'web', 'extension']) assert.equal(rows.platformKey(p), p);
  assert.equal(rows.platformKey(' Windows '), 'windows');
  assert.equal(rows.platformKey('ios'), 'unknown');
  assert.equal(rows.platformKey(null), 'unknown');
});

test('actions: Copy on text, links and images, Download on files, nothing on unknown', () => {
  assert.equal(rows.rowAction({ kind: 'text' }), 'copy');
  assert.equal(rows.rowAction({ kind: 'link' }), 'copy');
  assert.equal(rows.rowAction({ kind: 'image' }), 'copy');
  assert.equal(rows.rowAction({ kind: 'file' }), 'download');
  assert.equal(rows.rowAction({ kind: 'unknown' }), null);
});

test('status pill', () => {
  assert.deepEqual(rows.statusPill({ state: 'syncing' }), { label: 'Syncing', tone: 'live' });
  assert.deepEqual(rows.statusPill({ state: 'paused' }), { label: 'Paused', tone: 'paused' });
  assert.deepEqual(rows.statusPill({ state: 'offline' }), { label: 'Offline', tone: 'off' });
  assert.deepEqual(rows.statusPill({ state: 'serverTooOld' }), { label: 'Offline', tone: 'off' });
  assert.deepEqual(rows.statusPill({ state: 'appTooOld' }), { label: 'Offline', tone: 'off' });
  assert.deepEqual(rows.statusPill({ state: 'locked' }), { label: 'Locked', tone: 'off' });
  assert.deepEqual(rows.statusPill({ state: 'noVault' }), { label: 'Locked', tone: 'off' });
  assert.equal(rows.statusPill({ state: 'signedOut' }), null);
});

test('which view a status opens', () => {
  assert.equal(rows.viewFor({ state: 'signedOut' }), 'signin');
  assert.equal(rows.viewFor({ state: 'locked' }), 'unlock');
  assert.equal(rows.viewFor({ state: 'noVault' }), 'novault');
  for (const state of ['syncing', 'offline', 'paused', 'serverTooOld', 'appTooOld']) assert.equal(rows.viewFor({ state }), 'history');
});

test('filter and search: type first, then text and file names', () => {
  const list = [
    { id: 1, kind: 'text', text: 'Meeting moved' },
    { id: 2, kind: 'link', text: 'https://github.com/x' },
    { id: 3, kind: 'file', files: [{ name: 'Report.pdf', size: 1 }] },
    { id: 4, kind: 'image' },
    { id: 5, kind: 'unknown' },
  ];
  const ids = (filter, query) => rows.visibleRows(list, { filter, query }).map(r => r.id);
  assert.deepEqual(ids('all', ''), [1, 2, 3, 4, 5]);
  assert.deepEqual(ids('link', ''), [2]);
  assert.deepEqual(ids('all', 'report'), [3]);
  assert.deepEqual(ids('all', '  MEETING '), [1]);
  assert.deepEqual(ids('text', 'github'), []);
});

test('empty texts', () => {
  assert.equal(rows.emptyText({ query: 'x', filter: 'all', pinned: false }), 'No clips match that search.');
  assert.equal(rows.emptyText({ query: '', filter: 'file', pinned: false }), 'No files yet');
  assert.equal(rows.emptyText({ query: '', filter: 'link', pinned: true }), 'No pinned links');
  assert.equal(rows.emptyText({ query: '', filter: 'all', pinned: false }),
    'Nothing here yet. Copy something on a signed-in device and it shows up here.');
  assert.equal(rows.emptyText({ query: '', filter: 'all', pinned: true }),
    'No pinned clips. Pin a clip to keep it through Clear history and auto-delete.');
});

test('the list signature changes with a new copy or a pin, not otherwise', () => {
  const a = [{ id: 'a', lastCopiedAt: 't1', pinnedAt: null }, { id: 'b', lastCopiedAt: 't0', pinnedAt: null }];
  const same = a.map(r => ({ ...r, text: 'decrypted again' }));
  assert.equal(rows.listSignature(a), rows.listSignature(same));
  assert.notEqual(rows.listSignature(a), rows.listSignature([{ ...a[0], pinnedAt: 't2' }, a[1]]));
  assert.notEqual(rows.listSignature(a), rows.listSignature([a[1], { ...a[0], lastCopiedAt: 't3' }]));
});

test('the server origin pattern for the host permission', () => {
  assert.equal(rows.originPattern('https://paste.example.com/'), 'https://paste.example.com/*');
  assert.equal(rows.originPattern(' http://localhost:5202/sub '), 'http://localhost/*');
  assert.equal(rows.originPattern('paste.example.com'), null);
  assert.equal(rows.originPattern('ftp://x'), null);
});
