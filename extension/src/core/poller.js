// Watches the clipboard by reading it on a timer, since an extension gets no change event. Each read is reduced to a
// SHA-256 and compared with the previous one, so the clip itself is never kept here.

const enc = new TextEncoder();

async function fingerprint(item, digest) {
  if (!item.text && !item.png) return 'none';
  const text = enc.encode(item.text ?? '');
  const png = item.png ?? new Uint8Array(0);
  // Length-prefixed so text 'ab' + image 'c' never collides with text 'a' + image 'bc'.
  const bytes = new Uint8Array(8 + text.length + png.length);
  new DataView(bytes.buffer).setUint32(0, text.length);
  bytes.set(text, 4);
  new DataView(bytes.buffer).setUint32(4 + text.length, png.length);
  bytes.set(png, 8 + text.length);
  const hash = new Uint8Array(await digest(bytes));
  return Array.from(hash, b => b.toString(16).padStart(2, '0')).join('');
}

/**
 * One poll step, without the timer (the tests drive it directly).
 * `read()` returns `{text?, png?}`, `{}` for content it can't use (empty, or files copied in Explorer), or `null` when
 * the read itself failed. The first good read only sets the baseline: what was on the clipboard before the extension
 * started isn't a new copy (the desktop app doesn't upload it either). A failed read keeps the baseline; unusable
 * content moves it without a report, so copying the same text again afterwards counts as a copy.
 */
export function createPoller({ read, onChange, digest = b => crypto.subtle.digest('SHA-256', b), now = () => performance.now() }) {
  let last;
  let reads = 0;
  let busyMs = 0;
  return {
    async poll() {
      const started = now();
      try {
        const item = await read();
        if (item == null) return;
        const hash = await fingerprint(item, digest);
        const first = last === undefined;
        const changed = hash !== last;
        last = hash;
        if (first || !changed || hash === 'none') return;
        try {
          await onChange(item);
        } catch {
          // The engine reports its own errors; a throw here must not stop the next poll.
        }
      } finally {
        reads++;
        busyMs += now() - started;
      }
    },
    stats: () => ({ reads, busyMs }),
  };
}

/** Polls every `intervalMs` until `stop()`. A new read starts only after the previous one finished. */
export function startPollingWith({ read, onChange, intervalMs = 1000, now }) {
  const poller = createPoller({ read, onChange, now });
  let timer;
  let stopped = false;
  const loop = async () => {
    try {
      await poller.poll();
    } catch {
      // A read that throws is a failed read: try again on the next tick.
    }
    if (!stopped) timer = setTimeout(loop, intervalMs);
  };
  loop();
  return {
    stop() { stopped = true; clearTimeout(timer); },
    stats: poller.stats,
  };
}
