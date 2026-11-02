// What the popup shows, as plain functions of the engine's rows and status, so they run under node --test. The type
// filter and the file-row text are the web UI's (e2e.js), so the popup and the history page can't drift apart.

import { fileRowText, matchesFilter, filterEmptyText } from '../js/e2e.js';

const PLATFORMS = new Set(['windows', 'macos', 'linux', 'android', 'web', 'extension']);
const TITLE_LENGTH = 200;

// The web history page's texts (wwwroot/js/history.js).
export const EMPTY_HISTORY = 'Nothing here yet. Copy something on a signed-in device and it shows up here.';
export const EMPTY_PINNED = 'No pinned clips. Pin a clip to keep it through Clear history and auto-delete.';
export const NO_MATCH = 'No clips match that search.';
export const UNKNOWN_KIND = 'Update PrivatePaste to see this clip';

/** "just now", "4 min ago", "3 h ago", "2 d ago", then the date: as on the web history page. */
export function agoText(iso, now = Date.now()) {
  const at = new Date(iso);
  const seconds = (now - at.getTime()) / 1000;
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  if (seconds < 30 * 86400) return `${Math.floor(seconds / 86400)} d ago`;
  const pad = n => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`;
}

/** The row's one line: text with its whitespace folded, "Image · 1280 × 720", or the file summary. */
export function rowTitle(row) {
  switch (row.kind) {
    case 'text':
    case 'link': {
      const line = String(row.text ?? '').replace(/\s+/g, ' ').trim();
      return line.length <= TITLE_LENGTH ? line : line.slice(0, TITLE_LENGTH) + '…';
    }
    case 'image':
      return row.width && row.height ? `Image · ${row.width} × ${row.height}` : 'Image';
    case 'file':
      return fileRowText(row.files ?? []);
    default:
      return UNKNOWN_KIND;
  }
}

/** "Ivan · Pixel 9 · 4 min ago"; the person only comes with the row while the server has several. */
export const metaText = (row, now = Date.now()) =>
  [row.sourceUserName, row.sourceDeviceName, agoText(row.lastCopiedAt, now)].filter(Boolean).join(' · ');

export function platformKey(platform) {
  const key = String(platform ?? '').trim().toLowerCase();
  return PLATFORMS.has(key) ? key : 'unknown';
}

/** Files never go on the clipboard: their row downloads instead. */
export function rowAction(row) {
  if (row.kind === 'text' || row.kind === 'link' || row.kind === 'image') return 'copy';
  if (row.kind === 'file') return 'download';
  return null;
}

const PILLS = {
  syncing: { label: 'Syncing', tone: 'live' },
  paused: { label: 'Paused', tone: 'paused' },
  offline: { label: 'Offline', tone: 'off' },
  serverTooOld: { label: 'Offline', tone: 'off' },
  appTooOld: { label: 'Offline', tone: 'off' },
  locked: { label: 'Locked', tone: 'off' },
  noVault: { label: 'Locked', tone: 'off' },
};

/** The status pill in the popup's top bar; none while signed out. */
export const statusPill = status => PILLS[status?.state] ?? null;

export function viewFor(status) {
  switch (status?.state) {
    case 'signedOut': return 'signin';
    case 'locked': return 'unlock';
    case 'noVault': return 'novault';
    default: return 'history';
  }
}

/** The type filter first, then the search over the text (file names for a file clip), as on the web. */
export function visibleRows(rows, { filter = 'all', query = '' } = {}) {
  const q = query.trim().toLowerCase();
  return rows.filter(row => matchesFilter(row.kind, filter)
    && (!q || (row.kind === 'file' ? (row.files ?? []).map(f => f.name).join('\n') : row.text ?? '').toLowerCase().includes(q)));
}

export function emptyText({ query, filter, pinned }) {
  if (query.trim()) return NO_MATCH;
  if (filter !== 'all') return filterEmptyText(filter, pinned);
  return pinned ? EMPTY_PINNED : EMPTY_HISTORY;
}

/** Changes when a clip arrives, is copied again, pinned, unpinned or deleted: when the list must be drawn again. */
export const listSignature = rows => rows.map(r => `${r.id}|${r.lastCopiedAt}|${r.pinnedAt ?? ''}`).join('\n');

/**
 * The host permission to ask for at sign-in: the server's scheme and host, any port (match patterns take no port).
 * Null for anything but an http(s) address; the engine then says what to enter.
 */
export function originPattern(serverUrl) {
  try {
    const url = new URL(String(serverUrl ?? '').trim());
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
    return `${url.protocol}//${url.hostname}/*`;
  } catch {
    return null;
  }
}
