// The extension's long-lived page: the service worker keeps it open, and it hosts what must keep running (the
// clipboard poller and the sync engine). It is never shown.

import * as clipboard from './platform/clipboard.js';
import { startPollingWith } from './core/poller.js';
import { startEngine } from './engine-host.js';

/** Calls `onChange({text?, png?})` for each new copy, reading the clipboard every `intervalMs`. */
export const startPolling = (onChange, intervalMs = 1000) =>
  startPollingWith({ read: clipboard.readClipboard, onChange, intervalMs });

startEngine({ clipboard, startPolling });
