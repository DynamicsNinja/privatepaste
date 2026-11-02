// Watches the clipboard by reading it on a timer, since an extension gets no change event. Each read is reduced to a
// SHA-256 and compared with the previous one, so the clip itself is never kept here.

const enc = new TextEncoder();
const hex = buf => Array.from(new Uint8Array(buf), b => b.toString(16).padStart(2, '0')).join('');

// Text and image are hashed apart, straight from their own bytes: a screenshot can sit on the clipboard for minutes,
// and copying several MB into one buffer every second would be waste.
async function fingerprint(item, digest) {
  if (!item.text && !item.png) return 'none';
  const text = item.text ? hex(await digest(enc.encode(item.text))) : '-';
  const png = item.png ? hex(await digest(item.png)) : '-';
  return `t:${text} p:${png}`;
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
    /** @returns {Promise<number>} The ms spent reading and hashing (not in `onChange`, which is the engine's cost). */
    async poll() {
      const started = now();
      let item;
      let hash;
      let costMs;
      try {
        item = await read();
        if (item != null) hash = await fingerprint(item, digest);
      } finally {
        costMs = now() - started;
        reads++;
        busyMs += costMs;
      }
      if (item == null) return costMs;
      const first = last === undefined;
      const changed = hash !== last;
      last = hash;
      if (first || !changed || hash === 'none') return costMs;
      try {
        await onChange(item);
      } catch {
        // The engine reports its own errors; a throw here must not stop the next poll.
      }
      return costMs;
    },
    stats: () => ({ reads, busyMs }),
  };
}

/** Delay before the next read: `intervalMs`, stretched so reading and hashing take at most a tenth of the time. */
export const nextDelay = (intervalMs, costMs) => Math.max(intervalMs, costMs * 10);

/**
 * Polls until `stop()`. A new read starts only after the previous one finished. Small clips are read every
 * `intervalMs`; a big image on the clipboard (each read pulls the whole PNG) backs the poller off to about 10% of a core.
 */
export function startPollingWith({ read, onChange, intervalMs = 1000, now = () => performance.now(), setTimer = setTimeout, clearTimer = clearTimeout }) {
  const poller = createPoller({ read, onChange, now });
  let timer;
  let stopped = false;
  const loop = async () => {
    const started = now();
    let costMs;
    try {
      costMs = await poller.poll();
    } catch {
      // A read that throws is a failed read: try again on the next tick.
      costMs = now() - started;
    }
    if (!stopped) timer = setTimer(loop, nextDelay(intervalMs, costMs));
  };
  loop();
  return {
    stop() { stopped = true; clearTimer(timer); },
    stats: poller.stats,
  };
}
