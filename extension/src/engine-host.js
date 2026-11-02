// Builds the sync engine with the real dependencies of the offscreen document (IndexedDB, fetch, WebSocket, the system
// clock) and feeds it what the clipboard poller sees. offscreen.js calls startEngine once (ruling R-X1).
//
// The WebSocket: a browser can't set headers on the upgrade request, so the engine offers the protocol version and the
// token as subprotocols (docs/api.md, WebSocket). A server from before that answers 401 and the engine polls instead.

import { createApi } from './core/api.js';
import { openStore } from './core/store.js';
import { createEngine } from './core/engine.js';

export const STATUS_MESSAGE = 'engine.statusChanged';

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
