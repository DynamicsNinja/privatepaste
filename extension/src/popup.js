// The toolbar popup: sign in (in a tab), unlock, and the history with search, History/Pinned, the type chips, Copy and
// Download.
// It holds no keys and no clips at rest: every answer comes from the engine in the offscreen document (messages.js),
// which decrypts. Rows are built with DOM APIs and textContent only, so clip content never goes through innerHTML.
// Never log clip content, passphrases or tokens.

import { fromBase64 } from './core/base64.js';
import { browserName } from './core/browser.js';
import { createAsk, EngineError, FAILED } from './ui/ask.js';
import { historyWatch } from './ui/history-watch.js';
import { formatSize, nextFilter } from './js/e2e.js';
import * as ui from './ui/rows.js';

const REFRESH_MS = 2000;
const FEEDBACK_MS = 1500;
const NO_PERMISSION = 'Allow PrivatePaste to reach that server to sign in.';
const DOWNLOAD_FAILED = "Couldn't download the file. Try again.";
const LOAD_FAILED = "Couldn't load your history.";

const $ = id => document.getElementById(id);
// The default device name is this browser's name ("Edge", "Chrome", ...): one build serves both stores.
$('device-name').value = browserName();
const views = ['signin', 'unlock', 'novault', 'history', 'settings'];
const list = $('clips');
const search = $('search');
const chips = [...document.querySelectorAll('#chips .chip')];
const tabs = [...document.querySelectorAll('.tab')];

let status = null;
let view = null;
let settingsOpen = false;
let tab = 'history';
let filter = 'all';
let rows = [];          // the engine's local history, newest first (or most recently pinned first)
let older = [];         // rows from the server's older pages (Load more, Search older clips)
let cursor = null;      // the engine's cursor for the next older page; null once there are no more
let signature = '';
let generation = 0;     // bumped whenever the list is reloaded, so an answer still in flight is dropped
const thumbUrls = [];
const expanded = new Set();

const ask = createAsk(message => chrome.runtime.sendMessage(message));
const watch = historyWatch(ask);
// Thumbnails that failed earlier are asked for again once per popup open (the first load), not on every refresh.
let thumbsRetried = false;

/** Frees a download URL the offscreen document made (it also frees it after a minute, in case the popup closed). */
const release = url => ask({ type: 'engine.release', url }).catch(() => {});
// Long enough for the browser to start reading the file; a save dialog that closes the popup is covered by the minute.
const RELEASE_AFTER_MS = 10000;

function show(el, text) {
  el.textContent = text ?? '';
  el.hidden = !text;
}

function element(tag, className, text) {
  const el = document.createElement(tag);
  if (className) el.className = className;
  if (text !== undefined) el.textContent = text;
  return el;
}

const icon = (templateId, selector) => $(templateId).content.querySelector(selector).cloneNode(true);

// ---- views ----

// The same page runs as the toolbar popup and, for signing in, in a tab (ui.signInInTab says why).
const inTab = !!(await chrome.tabs.getCurrent());
document.body.classList.toggle('in-tab', inTab);
let handingOff = false;

/** Shows the sign-in tab (an open one, or a new one) and closes the popup. */
async function openSignInTab() {
  if (handingOff) return;
  handingOff = true;
  const url = chrome.runtime.getURL('popup.html');
  try {
    const [open] = await chrome.runtime.getContexts({ contextTypes: ['TAB'], documentUrls: [url] });
    if (open) {
      await chrome.tabs.update(open.tabId, { active: true });
      await chrome.windows.update(open.windowId, { focused: true });
    } else {
      await chrome.tabs.create({ url });
    }
  } finally {
    window.close();
  }
}

function render(next) {
  status = next;
  const pill = ui.statusPill(status);
  $('status').hidden = !pill;
  if (pill) {
    $('status-text').textContent = pill.label;
    $('status').dataset.tone = pill.tone;
  }

  let name = ui.viewFor(status);
  if (ui.signInInTab(name, { inTab })) {
    openSignInTab();
    return;
  }
  if (name === 'history' && settingsOpen) name = 'settings';
  if (name !== 'history' && name !== 'settings') settingsOpen = false;
  for (const v of views) $(`view-${v}`).hidden = v !== name;
  $('open-settings').hidden = name !== 'history';
  $('close-settings').hidden = name !== 'settings';

  if (name === 'unlock') show($('unlock-message'), status.message);
  if (name === 'novault') $('novault-message').textContent = status.message ?? '';
  if (name === 'history') show($('notice'), status.message);
  if (name === 'settings') {
    $('settings-server').textContent = status.serverUrl ?? '';
    $('settings-device').textContent = status.deviceName ?? '';
    // Every refresh tick sets it from the engine, except while a change is in flight (the box is disabled then): a tick
    // that read the old status must not flip the box back.
    if (!$('pause').disabled) $('pause').checked = !!status.paused;
  }

  const was = view;
  view = name;
  const unlocked = name === 'history' || name === 'settings';
  if (!unlocked && (was === 'history' || was === 'settings')) clearList();
  else if (name === 'history' && was !== 'history' && was !== 'settings') loadHistory();
  if (name === 'signin' && was !== 'signin') $('server').focus();
  if (name === 'unlock' && was !== 'unlock') $('passphrase').focus();
}

/** Runs a form's work with its submit button disabled; an EngineError shows in the form's error line. */
async function busy(form, errorLine, work) {
  const button = form.querySelector('button[type=submit]');
  button.disabled = true;
  form.setAttribute('aria-busy', 'true');
  try {
    errorLine.hidden = true;
    await work();
  } catch (e) {
    show(errorLine, e instanceof EngineError ? e.message : FAILED);
  } finally {
    button.disabled = false;
    form.removeAttribute('aria-busy');
  }
}

$('signin-form').addEventListener('submit', event => {
  event.preventDefault();
  const form = event.currentTarget;
  // The host permission must be asked for here, in the click, before anything is awaited. This form only runs in a tab
  // (ui.signInInTab), which stays open behind the prompt, so whatever the sign-in answers is shown.
  const pattern = ui.originPattern($('server').value);
  const granted = pattern ? chrome.permissions.request({ origins: [pattern] }).catch(() => false) : Promise.resolve(true);
  busy(form, $('signin-error'), async () => {
    if (!(await granted)) throw new EngineError(NO_PERMISSION);
    const next = await ask({
      type: 'engine.signIn',
      serverUrl: $('server').value,
      username: $('username').value,
      password: $('password').value,
      deviceName: $('device-name').value,
    });
    $('password').value = '';
    render(next);
  });
});

$('unlock-form').addEventListener('submit', event => {
  event.preventDefault();
  busy(event.currentTarget, $('unlock-error'), async () => {
    const next = await ask({ type: 'engine.unlock', passphrase: $('passphrase').value });
    $('passphrase').value = '';
    render(next);
  });
});

async function signOut(button) {
  button.disabled = true;
  try {
    settingsOpen = false;
    render(await ask({ type: 'engine.signOut' }));
  } catch (e) {
    show($('settings-error'), e.message);
  } finally {
    button.disabled = false;
  }
}
$('sign-out').addEventListener('click', event => signOut(event.currentTarget));
for (const button of document.querySelectorAll('button.sign-out')) button.addEventListener('click', () => signOut(button));

$('pause').addEventListener('change', async event => {
  const box = event.currentTarget;
  box.disabled = true;
  try {
    render(await ask({ type: 'engine.setPaused', paused: box.checked }));
    $('settings-error').hidden = true;
  } catch (e) {
    box.checked = !box.checked;
    show($('settings-error'), e.message);
  } finally {
    box.disabled = false;
  }
});

$('open-settings').addEventListener('click', () => { settingsOpen = true; render(status); });
$('close-settings').addEventListener('click', () => { settingsOpen = false; render(status); });

// ---- the list ----

function clearList() {
  generation++;
  rows = [];
  older = [];
  cursor = null;
  signature = '';
  expanded.clear();
  drawRows();
}

async function loadHistory() {
  const at = ++generation;
  try {
    await watch.changed();   // remember the version this list is drawn from
    const answer = await ask({ type: 'engine.history', pinned: tab === 'pinned', retryThumbs: !thumbsRetried });
    thumbsRetried = true;
    if (at !== generation) return;
    rows = answer.rows;
    older = [];
    cursor = answer.nextCursor;
    signature = ui.listSignature(rows);
    $('history-error').hidden = true;
    drawRows();
  } catch {
    if (at === generation) show($('history-error'), LOAD_FAILED);
  }
}

/**
 * While the popup is open: picks up the status, new clips and pins. Costs one status and one version answer while
 * nothing changes; the rows are asked for (and decrypted) only after the engine's history version moved.
 */
async function refresh() {
  try {
    render(await ask({ type: 'engine.status' }));
    if (view !== 'history') return;
    const at = generation;
    if (!(await watch.changed()) || at !== generation) return;
    const answer = await ask({ type: 'engine.history', pinned: tab === 'pinned' });
    if (at !== generation || ui.listSignature(answer.rows) === signature) return;
    rows = answer.rows;
    signature = ui.listSignature(rows);
    drawRows();
  } catch {
    watch.forget();   // try the rows again on the next tick; errors show on the next user action
  }
}

async function loadOlder(button, all) {
  const at = generation;
  button.disabled = true;
  try {
    do {
      const page = await ask({ type: 'engine.history', pinned: tab === 'pinned', cursor });
      if (at !== generation) return;
      older.push(...page.rows);
      cursor = page.nextCursor;
    } while (all && cursor);
    drawRows();
  } catch {
    if (at === generation) show($('history-error'), LOAD_FAILED);
  } finally {
    button.disabled = false;
  }
}
$('load-more').addEventListener('click', event => loadOlder(event.currentTarget, false));
// Search only sees what is loaded; this pages through the rest of the history so older matches show up too.
$('search-older').addEventListener('click', event => loadOlder(event.currentTarget, true));

const allRows = () => {
  const local = new Set(rows.map(r => r.id));
  return [...rows, ...older.filter(r => !local.has(r.id))];
};

function drawRows() {
  for (const url of thumbUrls.splice(0)) URL.revokeObjectURL(url);
  list.replaceChildren(...allRows().map(buildRow));
  applyFilter();
}

/** The chips and the search hide rows; the empty text and the paging buttons follow what is left. */
function applyFilter() {
  const query = search.value;
  const all = allRows();
  const visible = new Set(ui.visibleRows(all, { filter, query }).map(r => r.id));
  for (const li of list.children) li.hidden = !visible.has(li.dataset.id);
  const searching = Boolean(query.trim());
  $('empty').textContent = ui.emptyText({ query, filter, pinned: tab === 'pinned' });
  $('empty').hidden = visible.size > 0;
  $('load-more').hidden = searching || !cursor || all.length === 0;
  $('search-older').hidden = !searching || !cursor;
}

function buildRow(row) {
  const li = $('clip-row').content.firstElementChild.cloneNode(true);
  const known = ['text', 'link', 'image', 'file'].includes(row.kind);
  li.dataset.id = row.id;
  li.dataset.kind = known ? row.kind : 'unknown';
  const title = ui.rowTitle(row);

  const kind = li.querySelector('.kind');
  if (row.kind === 'image' && row.thumbPngBase64) {
    const url = URL.createObjectURL(new Blob([fromBase64(row.thumbPngBase64)], { type: 'image/png' }));
    thumbUrls.push(url);
    const img = element('img', 'thumb');
    img.alt = title;
    img.src = url;
    kind.append(img);
  } else {
    kind.append(icon('kind-icons', `[data-kind="${known ? row.kind : 'text'}"]`));
  }

  const titleEl = li.querySelector('.title');
  titleEl.textContent = title;
  titleEl.title = title;
  const meta = li.querySelector('.meta');
  if (row.pinnedAt && tab === 'history') {
    const pin = icon('action-icons', '[data-icon="pin"]');
    pin.setAttribute('role', 'img');
    pin.setAttribute('aria-label', 'Pinned');
    pin.removeAttribute('aria-hidden');
    meta.append(pin);
  }
  meta.append(icon('platform-icons', `[data-platform="${ui.platformKey(row.sourceDevicePlatform)}"]`), ui.metaText(row));

  const button = li.querySelector('button.act');
  const action = ui.rowAction(row);
  if (action === 'copy') {
    label(button, 'Copy');
    button.append(icon('action-icons', '[data-icon="copy"]'), icon('action-icons', '[data-icon="check"]'));
    button.addEventListener('click', () => copy(row, button));
  } else if (action === 'download') {
    button.append(icon('action-icons', '[data-icon="download"]'));
    if (row.files.length === 1) {
      label(button, `Download ${row.files[0].name}`);
      button.addEventListener('click', () => download(row, 0, button));
    } else {
      // Several files: the button opens the list, and each file has its own Download.
      label(button, 'Show files');
      button.setAttribute('aria-expanded', 'false');
      button.addEventListener('click', () => toggleFiles(li, row, button));
      if (expanded.has(row.id)) toggleFiles(li, row, button);
    }
  } else {
    button.remove();
  }
  return li;
}

function label(button, text) {
  button.setAttribute('aria-label', text);
  button.title = text;
}

function toggleFiles(li, row, button) {
  const open = li.querySelector('.file-lines');
  if (open) {
    open.remove();
    expanded.delete(row.id);
    button.setAttribute('aria-expanded', 'false');
    return;
  }
  const lines = element('ul', 'file-lines');
  row.files.forEach((file, n) => {
    const line = $('file-line').content.firstElementChild.cloneNode(true);
    line.querySelector('.file-name').textContent = file.name;
    line.querySelector('.file-size').textContent = formatSize(file.size);
    const get = line.querySelector('button');
    label(get, `Download ${file.name}`);
    get.addEventListener('click', () => download(row, n, get));
    lines.append(line);
  });
  li.append(lines);
  expanded.add(row.id);
  button.setAttribute('aria-expanded', 'true');
}

// ---- Copy and Download ----

function feedback(button, text, className) {
  const idle = button.getAttribute('aria-label');
  label(button, text);
  button.classList.add(className);
  setTimeout(() => {
    label(button, idle);
    button.classList.remove(className);
    button.disabled = false;
  }, FEEDBACK_MS);
}

function copy(row, button) {
  button.disabled = true;
  let done;
  if (row.kind === 'image') {
    // The background can't write an image, so the popup does, while it has the focus and the click. The engine hands
    // over the PNG it decrypted and remembers it, so the clipboard poller doesn't send the image back as a new copy.
    const png = (async () => {
      const copied = await ask({ type: 'engine.copy', id: row.id });
      if (!copied?.url) throw new Error('Not copied.');
      const { url } = copied;
      try {
        // The offscreen document's blob URL: same extension origin, so the popup can read it.
        return new Blob([await (await fetch(url)).arrayBuffer()], { type: 'image/png' });
      } finally {
        release(url);
      }
    })();
    done = navigator.clipboard.write([new ClipboardItem({ 'image/png': png })]);
  } else {
    done = ask({ type: 'engine.copy', id: row.id }).then(written => {
      if (!written) throw new Error('Not copied.');
    });
  }
  done.then(() => feedback(button, 'Copied', 'done'), () => feedback(button, 'Copy failed', 'failed'));
}

async function download(row, n, button) {
  button.disabled = true;
  try {
    // A blob URL made by the offscreen document (application/octet-stream, so the browser never opens or runs the
    // file), saved under the engine's safe name. The bytes never cross the message channel, whatever their size.
    const { url, name } = await ask({ type: 'engine.download', id: row.id, n });
    const a = element('a');
    a.href = url;
    a.download = name;
    document.body.append(a);
    a.click();
    a.remove();
    setTimeout(() => release(url), RELEASE_AFTER_MS);
    if ($('history-error').textContent === DOWNLOAD_FAILED) $('history-error').hidden = true;
  } catch {
    show($('history-error'), DOWNLOAD_FAILED);
  } finally {
    button.disabled = false;
  }
}

// ---- search, tabs, chips ----

search.addEventListener('input', applyFilter);

for (const t of tabs) {
  t.addEventListener('click', () => {
    if (tab === t.dataset.tab) return;
    tab = t.dataset.tab;
    for (const other of tabs) other.setAttribute('aria-selected', String(other === t));
    const placeholder = tab === 'pinned' ? 'Search pinned clips' : 'Search history';
    search.placeholder = placeholder;
    search.setAttribute('aria-label', placeholder);
    clearList();
    loadHistory();
  });
}

for (const chip of chips) {
  const kind = chip.dataset.filter;
  const slot = chip.querySelector('.chip-icon');
  if (slot) slot.append(icon('kind-icons', `[data-kind="${kind}"]`));
  chip.addEventListener('click', () => {
    filter = nextFilter(filter, kind);
    for (const other of chips) other.setAttribute('aria-pressed', String(other.dataset.filter === filter));
    applyFilter();
  });
}

// The engine broadcasts each status change; the timer catches new clips and pins, which have no event.
chrome.runtime.onMessage.addListener(message => {
  if (message?.type === 'engine.statusChanged' && message.status) render(message.status);
});
setInterval(refresh, REFRESH_MS);

ask({ type: 'engine.status' }).then(render, () => render({ state: 'signedOut' }));
