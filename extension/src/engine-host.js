// Placeholder until the sync engine is merged, which replaces this file: it runs the poller so the clipboard path is
// exercised, and drops every change.
export function startEngine({ startPolling }) {
  startPolling(() => {});
  return {};
}
