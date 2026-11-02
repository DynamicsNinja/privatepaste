// The service worker only keeps the offscreen document open: Edge stops a service worker after 30 s idle, but the
// offscreen document can keep polling the clipboard once a second. It is (re)created on install, on browser startup,
// whenever the worker wakes, and by a one-minute alarm in case Edge closed it.

const OFFSCREEN_URL = 'offscreen.html';
const WATCHDOG = 'offscreen-watchdog';

let creating;
async function ensureOffscreen() {
  const open = await chrome.runtime.getContexts({ contextTypes: ['OFFSCREEN_DOCUMENT'] });
  if (open.length) return;
  creating ??= chrome.offscreen
    .createDocument({
      url: OFFSCREEN_URL,
      reasons: ['CLIPBOARD'],
      justification: 'Reads and writes the clipboard to sync it with your other devices.',
    })
    .finally(() => { creating = undefined; });
  await creating;
}

function ensure() {
  ensureOffscreen().catch(e => console.warn('PrivatePaste: could not open the offscreen document:', e?.message));
}

async function ensureWatchdog() {
  if (!(await chrome.alarms.get(WATCHDOG))) await chrome.alarms.create(WATCHDOG, { periodInMinutes: 1 });
}

chrome.runtime.onInstalled.addListener(() => { ensureWatchdog(); ensure(); });
chrome.runtime.onStartup.addListener(() => { ensureWatchdog(); ensure(); });
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === WATCHDOG) ensure(); });
ensure();
