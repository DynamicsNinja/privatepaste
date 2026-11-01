// The history page: fetches clips, decrypts them in the browser and builds the rows. Rows are built with DOM APIs and
// textContent only, so clip content never goes through innerHTML. site.js calls the exports below for Copy, Pin and
// Delete. Never log decrypted content.

const shell = document.getElementById('history');
// Versioned URLs from the page (see Index.cshtml), so a deploy never pairs this file with an old module.
const e2e = await import(shell.dataset.e2e);
const { startVault } = await import(shell.dataset.vaultUi);

const PAGE_SIZE = 50;
const PREVIEW_LENGTH = 200;
const KINDS = new Set(['text', 'link', 'image']);
const PLATFORMS = new Set(['windows', 'macos', 'linux', 'android', 'web']);

const EMPTY_HISTORY = 'Nothing here yet. Copy something on a signed-in device and it shows up here.';
const EMPTY_PINNED = 'No pinned clips. Pin a clip to keep it through Clear history and auto-delete.';
const NO_MATCH = 'No clips match that search.';
const UNKNOWN_KIND = 'Update PrivatePaste to see this clip';
const UNREADABLE = "This clip can't be decrypted.";
const LOAD_FAILED = 'Could not load your history.';

const $ = id => document.getElementById(id);
const pinnedTab = shell.dataset.tab === 'pinned';
const view = $('clips-view');
const list = $('clips');
const empty = $('history-empty');
const pageError = $('history-error');
const loadMore = $('load-more');
const searchOlder = $('search-older');
const searchInput = $('search-input');

let vault = null;
let entries = []; // { clip, content, row, thumbUrl }, in list order
let cursor = null;
let exhausted = false;
// Bumped on Lock, so a page or thumbnail still in flight is dropped instead of drawn into the locked page.
let generation = 0;

/** fetch with the protocol header, and the antiforgery token on anything but a GET. */
export function api(path, init = {}) {
  const headers = new Headers(init.headers);
  headers.set('X-PrivatePaste-Protocol', '2');
  if (init.method && init.method !== 'GET')
    headers.set('RequestVerificationToken', document.querySelector('meta[name="csrf-token"]')?.content ?? '');
  return fetch(path, { ...init, headers });
}

// ---- decrypting ----

/** What a clip holds: { kind, text?, width?, height? }, kind 'unknown' for a newer kind and 'unreadable' on failure. */
async function open(clip) {
  if (!KINDS.has(clip.kind)) return { kind: 'unknown' };
  try {
    if (clip.keyId !== vault.keyId) throw new Error('Sealed under another key.');
    const payload = await e2e.openPayload(vault, clip.payload);
    return {
      kind: clip.kind,
      text: typeof payload.text === 'string' ? payload.text : '',
      width: payload.width,
      height: payload.height,
    };
  } catch {
    return { kind: 'unreadable' };
  }
}

async function fetchBytes(path) {
  const response = await api(path);
  if (!response.ok) throw new Error(`${path.replace(/[0-9a-f-]{36}/, '{id}')} failed (${response.status})`);
  return new Uint8Array(await response.arrayBuffer());
}

/** For Copy: the restored clip's content as a Blob, text/plain or image/png. */
export async function clipContent(clip) {
  if (!vault) throw new Error('The history is locked.');
  const content = await open(clip);
  if (content.kind === 'image') {
    const png = await e2e.decryptBlob(vault, await fetchBytes(`/api/clips/${encodeURIComponent(clip.id)}/blobs/0`));
    return new Blob([png], { type: 'image/png' });
  }
  if (content.kind === 'text' || content.kind === 'link') return new Blob([content.text], { type: 'text/plain' });
  throw new Error('This clip has nothing to copy.');
}

// ---- rows ----

const preview = text => (text.length <= PREVIEW_LENGTH ? text : text.slice(0, PREVIEW_LENGTH) + '...');

/** The URL to link to, only for http(s); anything else (javascript:, data:, ...) stays plain text. */
function safeHref(text) {
  try {
    const url = new URL(text.trim());
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null;
  } catch {
    return null;
  }
}

function element(tag, className, text) {
  const el = document.createElement(tag);
  if (className) el.className = className;
  if (text !== undefined) el.textContent = text;
  return el;
}

const icon = (templateId, selector) => $(templateId).content.querySelector(selector).cloneNode(true);

function agoText(at) {
  const seconds = (Date.now() - at.getTime()) / 1000;
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  if (seconds < 30 * 86400) return `${Math.floor(seconds / 86400)} d ago`;
  const pad = n => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`;
}

function body(entry) {
  const { clip, content } = entry;
  switch (content.kind) {
    case 'image': {
      const label = content.width && content.height ? `Image clip ${content.width} x ${content.height}` : 'Image clip';
      if (!clip.hasThumb) return element('p', 'text muted', label);
      const img = element('img', 'thumb');
      img.alt = label;
      loadThumb(entry, img);
      return img;
    }
    case 'link': {
      const href = safeHref(content.text);
      if (!href) return element('pre', 'text', preview(content.text));
      const a = element('a', null, preview(content.text));
      a.href = href;
      a.rel = 'noopener noreferrer';
      a.target = '_blank';
      return a;
    }
    case 'text':
      return element('pre', 'text', preview(content.text));
    case 'unknown':
      return element('p', 'text muted', UNKNOWN_KIND);
    default:
      return element('p', 'text muted', UNREADABLE);
  }
}

async function loadThumb(entry, img) {
  const at = generation;
  try {
    const png = await e2e.openSmall(vault, 'thumb', await fetchBytes(`/api/clips/${encodeURIComponent(entry.clip.id)}/thumb`));
    if (at !== generation) return;
    entry.thumbUrl = URL.createObjectURL(new Blob([png], { type: 'image/png' }));
    img.src = entry.thumbUrl;
  } catch (error) {
    console.error(error);
  }
}

function setPinned(row, clip) {
  const pinned = Boolean(clip.pinnedAt);
  row.querySelector('.pin-marker').hidden = !pinned || pinnedTab;
  const toggle = row.querySelector('.pin-toggle');
  toggle.setAttribute('aria-pressed', String(pinned));
  toggle.querySelector('.sr-only').textContent = pinned ? 'Unpin' : 'Pin';
}

function buildRow(entry) {
  const { clip, content } = entry;
  const row = $('clip-row').content.firstElementChild.cloneNode(true);
  row.dataset.id = clip.id;
  row.dataset.kind = KINDS.has(content.kind) ? content.kind : 'unknown';
  row.querySelector('.kind').append(icon('kind-icons', `[data-kind="${KINDS.has(content.kind) ? content.kind : 'text'}"]`));

  const meta = row.querySelector('.meta');
  meta.before(body(entry));
  const chip = meta.querySelector('.person-chip');
  if (clip.sourceUserName) chip.textContent = clip.sourceUserName;
  else chip.remove();
  const platform = String(clip.sourceDevicePlatform ?? '').trim().toLowerCase();
  const time = element('time', null, agoText(new Date(clip.lastCopiedAt)));
  time.dateTime = clip.lastCopiedAt;
  meta.append(icon('platform-icons', `[data-platform="${PLATFORMS.has(platform) ? platform : 'unknown'}"]`),
    `${clip.sourceDeviceName}, `, time);

  if (!KINDS.has(content.kind)) row.querySelector('.copy').remove();
  setPinned(row, clip);
  return row;
}

// ---- the list ----

/** Applies the search to the loaded rows and shows the empty text and the paging buttons that fit. */
function refresh() {
  const query = searchInput.value.trim().toLowerCase();
  let visible = 0;
  entries.forEach((entry, index) => {
    const match = !query || (entry.content.text ?? '').toLowerCase().includes(query);
    entry.row.hidden = !match;
    // Only the true top of the list gets the "just arrived" highlight, not the first search hit.
    entry.row.classList.toggle('newest', index === 0 && !query);
    if (match) visible++;
  });
  empty.textContent = query ? NO_MATCH : pinnedTab ? EMPTY_PINNED : EMPTY_HISTORY;
  empty.hidden = visible > 0 || (!query && !exhausted);
  loadMore.hidden = exhausted || Boolean(query);
  searchOlder.hidden = exhausted || !query;
}

async function loadPage() {
  const at = generation;
  const params = new URLSearchParams({ limit: String(PAGE_SIZE) });
  if (pinnedTab) params.set('pinned', 'true');
  if (cursor) params.set('cursor', cursor);
  const response = await api(`/api/clips?${params}`);
  if (!response.ok) throw new Error(`History fetch failed (${response.status})`);
  const page = await response.json();
  const contents = await Promise.all(page.items.map(open));
  if (at !== generation) return;

  cursor = page.nextCursor ?? null;
  exhausted = !cursor;
  page.items.forEach((clip, i) => {
    // A clip copied again while paging moves up and can come back on a later page.
    if (entries.some(e => e.clip.id === clip.id)) return;
    const entry = { clip, content: contents[i] };
    entry.row = buildRow(entry);
    entries.push(entry);
    list.append(entry.row);
  });
  refresh();
}

function fail(error) {
  console.error(error);
  pageError.textContent = LOAD_FAILED;
  pageError.hidden = false;
}

async function whileBusy(button, work) {
  button.disabled = true;
  try {
    await work();
  } catch (error) {
    fail(error);
  } finally {
    button.disabled = false;
  }
}

/** After Pin or Unpin: the server's updated clip. */
export function clipChanged(clip) {
  const entry = entries.find(e => e.clip.id === clip.id);
  if (!entry) return;
  if (pinnedTab && !clip.pinnedAt) {
    clipRemoved(clip.id);
    return;
  }
  entry.clip = clip;
  setPinned(entry.row, clip);
}

/** After Delete. */
export function clipRemoved(id) {
  const index = entries.findIndex(e => e.clip.id === id);
  if (index < 0) return;
  const [entry] = entries.splice(index, 1);
  entry.row.remove();
  if (entry.thumbUrl) URL.revokeObjectURL(entry.thumbUrl);
  refresh();
}

function clear() {
  generation++;
  for (const entry of entries) if (entry.thumbUrl) URL.revokeObjectURL(entry.thumbUrl);
  entries = [];
  list.replaceChildren();
  cursor = null;
  exhausted = false;
}

loadMore.addEventListener('click', () => whileBusy(loadMore, loadPage));
// Search only sees what is loaded; this pages through the rest of the history so older matches show up too.
searchOlder.addEventListener('click', () => whileBusy(searchOlder, async () => {
  while (!exhausted && vault) await loadPage();
}));
searchInput.addEventListener('input', refresh);
$('search').addEventListener('submit', event => {
  event.preventDefault();
  refresh();
});

startVault(e2e, api, {
  onUnlocked(unlocked) {
    vault = unlocked;
    pageError.hidden = true;
    view.hidden = false;
    loadPage().catch(fail);
  },
  onLocked() {
    vault = null;
    clear();
    view.hidden = true;
    pageError.hidden = true;
  },
}).catch(fail);
