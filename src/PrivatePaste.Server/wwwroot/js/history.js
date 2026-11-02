// The history page: fetches clips, decrypts them in the browser and builds the rows. Rows are built with DOM APIs and
// textContent only, so clip content never goes through innerHTML. site.js calls the exports below for Copy, Pin and
// Delete. Never log decrypted content.

const shell = document.getElementById('history');
// Versioned URLs from the page (see Index.cshtml), so a deploy never pairs this file with an old module.
const e2e = await import(shell.dataset.e2e);
const { startVault } = await import(shell.dataset.vaultUi);

const PAGE_SIZE = 50;
const PREVIEW_LENGTH = 200;
const KINDS = new Set(['text', 'link', 'image', 'file']);
const PLATFORMS = new Set(['windows', 'macos', 'linux', 'android', 'web']);

const EMPTY_HISTORY = 'Nothing here yet. Copy something on a signed-in device and it shows up here.';
const EMPTY_PINNED = 'No pinned clips. Pin a clip to keep it through Clear history and auto-delete.';
const NO_MATCH = 'No clips match that search.';
const UNKNOWN_KIND = 'Update PrivatePaste to see this clip';
const UNREADABLE = "This clip can't be decrypted.";
const LOAD_FAILED = "Couldn't load your history.";

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
const loadedIds = new Set();
let cursor = null;
let exhausted = false;
// Bumped on Lock, so a page or thumbnail still in flight is dropped instead of drawn into the locked page.
let generation = 0;

/**
 * True, after asking vault-ui.js to forget the key, when the server holds a clip sealed under another key: the owner
 * reset encryption since this tab unlocked (the reset deletes every older clip, and uploads under an old key are
 * refused).
 */
function wasReset(clip) {
  if (!vault || !clip.keyId || clip.keyId === vault.keyId) return false;
  document.dispatchEvent(new Event('pp:vault-reset'));
  return true;
}

/** fetch with the protocol header, and the antiforgery token on anything but a GET. */
export function api(path, init = {}) {
  const headers = new Headers(init.headers);
  headers.set('X-PrivatePaste-Protocol', '2');
  if (init.method && init.method !== 'GET')
    headers.set('RequestVerificationToken', document.querySelector('meta[name="csrf-token"]')?.content ?? '');
  return fetch(path, { ...init, headers });
}

// ---- decrypting ----

/**
 * What a clip holds: { kind, text?, width?, height?, files? }, kind 'unknown' for a newer kind and 'unreadable' on failure.
 * files: [{ name, size }] with names already made safe (they come from another device), in blob order.
 */
async function open(clip) {
  if (!KINDS.has(clip.kind)) return { kind: 'unknown' };
  try {
    if (clip.keyId !== vault.keyId) throw new Error('Sealed under another key.');
    const payload = await e2e.openPayload(vault, clip.payload);
    if (clip.kind === 'file') {
      if (!Array.isArray(payload.files) || payload.files.length === 0) throw new Error('A file clip without files.');
      const files = payload.files.map(f => ({
        name: e2e.safeFileName(typeof f?.name === 'string' ? f.name : ''),
        size: Number.isSafeInteger(f?.size) && f.size >= 0 ? f.size : 0,
      }));
      return { kind: 'file', files, text: files.map(f => f.name).join('\n') };
    }
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
  if (wasReset(clip)) throw new Error('Encryption was reset.');
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
      thumbOf.set(img, entry);
      thumbObserver.observe(img);
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
    case 'file': {
      const box = element('div', 'files');
      box.append(element('p', 'text', e2e.fileRowText(content.files)));
      const lines = element('ul', 'file-lines');
      content.files.forEach((file, index) => {
        const line = $('file-line').content.firstElementChild.cloneNode(true);
        line.querySelector('.file-name').textContent = file.name;
        line.querySelector('.file-size').textContent = e2e.formatSize(file.size);
        const button = line.querySelector('.file-download');
        const label = `Download ${file.name}`;
        button.setAttribute('aria-label', label);
        button.title = label;
        button.dataset.index = String(index);
        lines.append(line);
      });
      box.append(lines);
      return box;
    }
    case 'unknown':
      return element('p', 'text muted', UNKNOWN_KIND);
    default:
      return element('p', 'text muted', UNREADABLE);
  }
}

// Thumbnails are fetched and decrypted only once their row comes near the screen (hidden rows never do), so paging
// through the history for a search doesn't download every thumbnail.
const thumbOf = new WeakMap();
const thumbObserver = new IntersectionObserver(seen => {
  for (const { isIntersecting, target } of seen) {
    if (!isIntersecting) continue;
    thumbObserver.unobserve(target);
    loadThumb(thumbOf.get(target), target);
  }
}, { rootMargin: '400px 0px' });

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

  // A file clip has no Copy: its files can't go on the browser clipboard; each line has its own Download.
  if (!KINDS.has(content.kind) || content.kind === 'file') row.querySelector('.copy').remove();
  setPinned(row, clip);
  return row;
}

// ---- the list ----

/** Applies the search to the loaded rows and shows the empty text and the paging buttons that fit. */
function refresh() {
  const query = searchInput.value.trim().toLowerCase();
  let visible = 0;
  entries.forEach((entry, index) => {
    // For a file clip, text is its file names.
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
  if (at !== generation || page.items.some(wasReset)) return;

  cursor = page.nextCursor ?? null;
  exhausted = !cursor;
  page.items.forEach((clip, i) => {
    // A clip copied again while paging moves up and can come back on a later page.
    if (loadedIds.has(clip.id)) return;
    loadedIds.add(clip.id);
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
  if (!entry || wasReset(clip)) return;
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
  loadedIds.delete(id);
  const img = entry.row.querySelector('img.thumb');
  if (img) thumbObserver.unobserve(img);
  entry.row.remove();
  if (entry.thumbUrl) URL.revokeObjectURL(entry.thumbUrl);
  refresh();
}

function clear() {
  generation++;
  thumbObserver.disconnect();
  loadedIds.clear();
  for (const entry of entries) if (entry.thumbUrl) URL.revokeObjectURL(entry.thumbUrl);
  entries = [];
  list.replaceChildren();
  cursor = null;
  exhausted = false;
}

// ---- file downloads ----

/**
 * Fetches, decrypts and saves one file of a file clip. The bytes are saved as application/octet-stream under the safe
 * name, so the browser never opens or runs the file, whatever its extension or the mime the sender claimed.
 */
async function download(entry, index) {
  const { name } = entry.content.files[index];
  const bytes = await e2e.decryptBlob(vault, await fetchBytes(`/api/clips/${encodeURIComponent(entry.clip.id)}/blobs/${index}`));
  const url = URL.createObjectURL(new Blob([bytes], { type: 'application/octet-stream' }));
  const a = element('a');
  a.href = url;
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  // The download has started by the time the click returns; revoking later keeps slow browsers from losing it.
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

list.addEventListener('click', event => {
  const button = event.target.closest('button.file-download');
  if (!button || !vault) return;
  const entry = entries.find(e => e.clip.id === button.closest('.clip').dataset.id);
  if (!entry) return;
  const at = generation;
  whileBusy(button, async () => {
    try {
      await download(entry, Number(button.dataset.index));
    } catch (error) {
      console.error(error);
      // Same message as a clip that can't be opened; the files are gone from the row.
      if (at === generation) entry.row.querySelector('.files')?.replaceWith(element('p', 'text muted', UNREADABLE));
    }
  });
});

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
