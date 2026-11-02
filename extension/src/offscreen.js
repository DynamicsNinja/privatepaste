// The extension's long-lived page: the service worker keeps it open, and it hosts what must keep running (the
// clipboard poller and the sync engine). It is never shown. The popup talks to the engine through runtime messages,
// answered here (messages.js), so the keys never leave this page.

import * as clipboard from './platform/clipboard.js';
import { startPollingWith } from './core/poller.js';
import { startEngine } from './engine-host.js';
import { handleEngineMessage, replyWith } from './messages.js';

/** Calls `onChange({text?, png?})` for each new copy, reading the clipboard every `intervalMs`. */
export const startPolling = (onChange, intervalMs = 1000) =>
  startPollingWith({ read: clipboard.readClipboard, onChange, intervalMs });

const engine = startEngine({ clipboard, startPolling });

chrome.runtime.onMessage.addListener((message, _sender, reply) => {
  const answer = handleEngineMessage(engine, message);
  if (!answer) return false;   // someone else's message (the fake clipboard's, a status broadcast)
  replyWith(answer, reply);
  return true;
});
