// Builds the sync engine with the real dependencies of the offscreen document (IndexedDB, fetch, WebSocket, the system
// clock) and feeds it what the clipboard poller sees. offscreen.js calls startEngine once (ruling R-X1).
//
// The WebSocket: a browser can't set the Authorization or X-PrivatePaste-Protocol header on the upgrade request, and the
// server's /ws takes the token only from those headers. The service worker adds them with a declarativeNetRequest
// session rule (ws-auth.js), which the offscreen document can't call itself: before each socket the engine asks for it
// with a runtime message. If the socket still fails, the engine polls /api/sync every 2 s instead.

import { createApi } from './core/api.js';
import { openStore } from './core/store.js';
import { createEngine } from './core/engine.js';
import { SET_WS_AUTH, CLEAR_WS_AUTH } from './ws-auth.js';

export const STATUS_MESSAGE = 'engine.statusChanged';

const ask = async message => {
  const reply = await chrome.runtime.sendMessage(message);
  if (!reply?.ok) throw new Error('The service worker could not set the socket headers.');
};

/**
 * @param {{clipboard: {readClipboard, writeText, writeImage}, startPolling: (onChange: (item) => void, intervalMs?: number) => void}} platform
 * @returns the engine (see core/engine.js), with `ready`: a promise that settles once the stored state is loaded.
 */
export function startEngine({ clipboard, startPolling }) {
  const engine = createEngine({
    api: ({ serverUrl, token }) => createApi({ serverUrl, token, fetch: (url, init) => fetch(url, init) }),
    store: openStore(indexedDB),
    clipboard,
    clock: { now: () => Date.now(), setTimeout: (fn, ms) => setTimeout(fn, ms), clearTimeout: id => clearTimeout(id) },
    WebSocket,
    wsAuth: {
      set: (url, token) => ask({ type: SET_WS_AUTH, url, token }),
      clear: () => ask({ type: CLEAR_WS_AUTH }),
    },
    // The popup listens for this while it is open; nobody listening is fine.
    onStatus: status => {
      try {
        chrome.runtime.sendMessage({ type: STATUS_MESSAGE, status })?.catch?.(() => {});
      } catch { /* no receiver */ }
    },
  });
  // A store that can't be opened leaves the engine signed out; copies are then ignored rather than failing each poll.
  const ready = engine.start().catch(() => {});
  startPolling(item => { ready.then(() => engine.onLocalChange(item)); });
  return Object.assign(engine, { ready });
}
