// The extension's sync engine: the equivalent of the desktop's SyncEngine and ClipboardCoordinator (Client.Core), and
// docs/api.md "Client rules" line by line. Every dependency is injected, so it runs under node --test with fakes.
//
// Per connection: read server-info (404: the server is too old), check the vault key id against GET /api/vault, catch up
// with /api/sync (applying at most the newest caught-up clip), flush the offline queue, then follow the WebSocket, pinging
// every 20 s. When the socket can't be opened (a browser can't put the bearer token on the upgrade request unless
// something adds it, see engine-host.js) the connection polls /api/sync every 2 s instead, and tries the socket again
// every minute. Any failure goes offline and reconnects after 1, 2, 5, 10, then 30 s.
//
// Echo and ordering rules:
// - A clip this engine writes to the clipboard is remembered by content hash, so the poll reading it back isn't uploaded.
// - A live clip is applied only when it is newer than the last applied clip and than the last local copy (the server
//   time of its upload, or the copy time of a queued one), never from this device, never a pin change (live: false).
// - Only text and links go onto the clipboard: an extension can't write images or files from the background, so those
//   go to the history (the popup copies images on click), and lastAppliedAt is about text and links only.
// - Catch-up applies at most the newest caught-up clip, only when it tops the history, nothing is queued, it is newer
//   than both times above, and it isn't a pin change; the first catch-up after sign-in or unlock and the first after a
//   pause are history only.
//
// Never log or report clip content, passphrases, keys or tokens.

import { ApiError, isTransient } from './api.js';
import { inspectLocal, sealLocal, decodeClip, DEFAULT_LIMITS, KNOWN_KINDS, contentHashText, contentHashPixels } from './codec.js';
import { pngToRgba } from './image.js';
import { unwrapWithPassphrase, WrongPassphraseError, encryptBlob, decryptBlob, openSmall } from '../js/e2e.js';

export const MESSAGES = Object.freeze({
  serverTooOld: 'Update your PrivatePaste server to keep syncing.',
  appTooOld: 'Update PrivatePaste to keep syncing.',
  reset: 'Encryption was reset. Enter the new passphrase.',
  askOwner: 'Ask the owner to set up encryption.',
  ownerSetUp: 'Set up encryption on the web first.',
  wrongPassphrase: "That passphrase doesn't unlock this server's history.",
  unsupportedWrap: "This server's passphrase settings aren't supported. Update PrivatePaste.",
  signInFirst: 'Sign in to PrivatePaste first.',
  syncFailed: "Couldn't sync a clip after several tries.",
});

export const DEFAULT_DEVICE_NAME = 'Edge';
export const BACKOFF_MS = Object.freeze([1000, 2000, 5000, 10000, 30000]);
export const PING_MS = 20000;
export const PONG_TIMEOUT_MS = 10000;
export const OPEN_TIMEOUT_MS = 10000;
export const POLL_MS = 2000;
export const POLL_SOCKET_RETRY_MS = 60000;
export const FLUSH_MS = 30000;
export const ECHO_WINDOW_MS = 10000;
export const DROP_AFTER_ATTEMPTS = 10;
const SERVER_CURSOR = 'server:';
const REVOKED_CLOSE = 1008;

const time = iso => (typeof iso === 'string' ? Date.parse(iso) || 0 : 0);
const utf8 = new TextEncoder();
const fromUtf8 = new TextDecoder();

/** Ends a connection and says why: 'revoked', 'appTooOld', 'reset' or 'noVault'. */
class Stop extends Error {
  constructor(kind) {
    super(kind);
    this.kind = kind;
  }
}

function stopOf(e) {
  if (e instanceof Stop) return e.kind;
  if (e instanceof ApiError) {
    if (e.status === 401) return 'revoked';
    if (e.status === 426) return 'appTooOld';
    if (e.status === 409 && e.title === 'Encryption was reset') return 'reset';
  }
  return null;
}

/**
 * @param deps.api        ({serverUrl, token?}) => client, as createApi with fetch bound
 * @param deps.store      openStore(...)
 * @param deps.clipboard  {writeText(text): Promise<boolean>, writeImage(png): Promise<boolean>}
 * @param deps.clock      {now(): number, setTimeout(fn, ms), clearTimeout(id)}
 * @param deps.WebSocket  a WebSocket constructor
 * @param deps.onStatus   called with a copy of status() on every change
 */
export function createEngine({ api: makeApi, store, clipboard, clock, WebSocket, onStatus = () => {} }) {
  let session = null;
  let vault = null;
  let state = null;
  let running = false;
  let current = { state: 'signedOut' };

  let gen = 0;
  let socket = null;
  const sleepers = new Set();

  let lastLocalHash = null;
  const echoes = new Map();          // content hash -> when this engine wrote it
  let lastApplied = null;            // {id, seq}
  let resumingFromPause = false;
  const tombstones = new Map();      // id -> seq of its deletion
  const extra = new Map();           // clips from server history pages (not in the cache), for copy and download
  const thumbs = new Map();          // `${id}:${hash}` -> thumbnail PNG

  let chain = Promise.resolve();
  /** Runs fn after everything queued before it: captures, catch-ups, flushes and messages never interleave. */
  const serial = fn => {
    const run = chain.then(fn);
    chain = run.catch(() => {});
    return run;
  };

  // ---- status ----

  function setStatus(stateName, message) {
    const next = message ? { state: stateName, message } : { state: stateName };
    if (current.state === next.state && current.message === next.message) return;
    current = next;
    try { onStatus(status()); } catch { /* a listener must not break the engine */ }
  }

  /** Shows a notice (e.g. why a copy wasn't sent) under the current state, until the state changes. */
  const notify = message => setStatus(current.state, message);

  function status() {
    return {
      ...current,
      serverUrl: session?.serverUrl ?? null,
      deviceName: session?.deviceName ?? null,
      paused: !!state?.paused,
    };
  }

  const currentApi = () => makeApi({ serverUrl: session.serverUrl, token: session.token });
  const limits = () => state?.serverInfo?.limits ?? DEFAULT_LIMITS;
  const kinds = () => state?.serverInfo?.kinds ?? KNOWN_KINDS;
  const saveState = async partial => { state = await store.setState(partial); };

  // ---- lifecycle ----

  function endConnection() {
    gen++;
    const s = socket;
    socket = null;
    if (s) try { s.close(); } catch { /* already closed */ }
    for (const wake of sleepers) wake();
    sleepers.clear();
  }

  function sleep(ms, g) {
    return new Promise(resolve => {
      if (g !== gen) return resolve();
      const wake = () => { clock.clearTimeout(timer); sleepers.delete(wake); resolve(); };
      const timer = clock.setTimeout(wake, ms);
      sleepers.add(wake);
    });
  }

  function launch() {
    endConnection();
    if (current.state !== 'syncing') setStatus('offline');
    loop(gen);
  }

  async function loop(g) {
    let attempt = 0;
    while (g === gen) {
      let result;
      try {
        result = await connectOnce(g);
      } catch {
        result = {};   // an unexpected failure: back off like a dropped link
      }
      if (g !== gen) return;
      if (result.stop) return handleStop(result.stop);
      if (result.again) { attempt = 0; continue; }
      if (result.stable) attempt = 0;
      if (result.serverTooOld) setStatus('serverTooOld', MESSAGES.serverTooOld);
      else setStatus('offline');
      await sleep(result.serverTooOld ? BACKOFF_MS.at(-1) : BACKOFF_MS[Math.min(attempt++, BACKOFF_MS.length - 1)], g);
    }
  }

  async function connectOnce(g) {
    const api = currentApi();
    try {
      let info;
      try {
        info = await api.serverInfo();
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) return { serverTooOld: true };
        throw e;
      }
      if (g !== gen) return {};
      await saveState({ serverInfo: readServerInfo(info) });

      const wrap = await api.vault();
      if (g !== gen) return {};
      if (!wrap) return { stop: 'noVault' };
      if (wrap.keyId !== vault.keyId) return { stop: 'reset' };

      await serial(() => catchUp(g, api, true));
      await serial(() => flushQueue(g, api));
    } catch (e) {
      const stop = stopOf(e);
      return stop ? { stop } : {};
    }
    if (g !== gen) return {};
    const ws = await openSocket(g, api);
    if (g !== gen) return {};
    return ws ? followSocket(g, api, ws) : poll(g, api);
  }

  function readServerInfo(info) {
    const l = info?.limits ?? {};
    const positive = v => (Number.isFinite(v) && v > 0 ? v : null);
    return {
      kinds: Array.isArray(info?.kinds) ? info.kinds.filter(k => typeof k === 'string') : KNOWN_KINDS,
      limits: {
        textKb: positive(l.textKb) ?? DEFAULT_LIMITS.textKb,
        imageMb: positive(l.imageMb) ?? DEFAULT_LIMITS.imageMb,
        fileMb: positive(l.fileMb) ?? DEFAULT_LIMITS.fileMb,
      },
    };
  }

  function openSocket(g, api) {
    return new Promise(resolve => {
      let ws;
      try {
        ws = new WebSocket(api.socketUrl());
      } catch {
        return resolve(null);
      }
      socket = ws;
      let settled = false;
      const done = value => {
        if (settled) return;
        settled = true;
        clock.clearTimeout(timer);
        if (!value && socket === ws) socket = null;
        resolve(g === gen ? value : null);
      };
      const timer = clock.setTimeout(() => {
        try { ws.close(); } catch { /* never opened */ }
        done(null);
      }, OPEN_TIMEOUT_MS);
      ws.onopen = () => done(ws);
      ws.onerror = () => done(null);
      ws.onclose = () => done(null);
    });
  }

  function followSocket(g, api, ws) {
    return new Promise(resolve => {
      const openedAt = clock.now();
      let stop = null;
      let pongDue = null;
      let flushing = null;
      setStatus('syncing');

      const tick = clock.setTimeout(function every() {
        if (g !== gen) return;
        try { ws.send(JSON.stringify({ type: 'ping' })); } catch { /* the close handler takes over */ }
        pongDue ??= clock.setTimeout(() => { try { ws.close(); } catch { /* closed */ } }, PONG_TIMEOUT_MS);
        timers.ping = clock.setTimeout(every, PING_MS);
      }, PING_MS);
      const timers = { ping: tick };
      const flushTick = () => {
        timers.flush = clock.setTimeout(async () => {
          if (g !== gen) return;
          if (!flushing && (await store.queue()).length > 0) {
            flushing = serial(() => flushQueue(g, api)).catch(e => {
              stop ??= stopOf(e);
              if (stop) try { ws.close(); } catch { /* closed */ }
            }).finally(() => { flushing = null; });
          }
          flushTick();
        }, FLUSH_MS);
      };
      flushTick();

      ws.onmessage = event => {
        if (g !== gen) return;
        let msg;
        try {
          msg = JSON.parse(typeof event.data === 'string' ? event.data : '');
        } catch {
          return;   // not a message this client reads
        }
        if (msg?.type === 'pong') {
          clock.clearTimeout(pongDue);
          pongDue = null;
          return;
        }
        serial(() => handleMessage(g, msg)).catch(e => {
          stop ??= stopOf(e);
          if (stop) try { ws.close(); } catch { /* closed */ }
        });
      };
      ws.onerror = () => {};
      ws.onclose = event => {
        clock.clearTimeout(timers.ping);
        clock.clearTimeout(timers.flush);
        clock.clearTimeout(pongDue);
        if (socket === ws) socket = null;
        if (stop) return resolve({ stop });
        if (event?.code === REVOKED_CLOSE) return resolve({ stop: 'revoked' });
        resolve({ stable: clock.now() - openedAt >= PING_MS });
      };
    });
  }

  async function poll(g, api) {
    setStatus('syncing');
    const startedAt = clock.now();
    let flushedAt = startedAt;
    while (g === gen) {
      await sleep(POLL_MS, g);
      if (g !== gen) return {};
      try {
        await serial(() => catchUp(g, api, false));
        if (clock.now() - flushedAt >= FLUSH_MS) {
          flushedAt = clock.now();
          await serial(() => flushQueue(g, api));
        }
      } catch (e) {
        const stop = stopOf(e);
        return stop ? { stop } : {};
      }
      if (clock.now() - startedAt >= POLL_SOCKET_RETRY_MS) return { again: true, stable: true };
    }
    return {};
  }

  async function handleStop(kind) {
    endConnection();
    if (kind === 'revoked') return forget();
    if (kind === 'appTooOld') return setStatus('appTooOld', MESSAGES.appTooOld);
    if (kind === 'reset') return relock('locked', MESSAGES.reset);
    if (kind === 'noVault') {
      let owner = false;
      try { owner = !!(await currentApi().me())?.isOwner; } catch { /* the plainer message */ }
      return relock('noVault', owner ? MESSAGES.ownerSetUp : MESSAGES.askOwner);
    }
  }

  /** Deletes the vault key, the history cache and the queue, and asks for the passphrase (or for encryption). */
  async function relock(stateName, message) {
    endConnection();
    vault = null;
    await store.setVault(null);
    await store.clearCache();
    await store.clearQueue();
    tombstones.clear();
    extra.clear();
    thumbs.clear();
    lastApplied = null;
    await saveState({ highestSeq: 0, lastAppliedAt: null, lockState: stateName, lockMessage: message ?? null });
    setStatus(stateName, message);
  }

  /** Signed out or revoked: the token, vault keys, history cache and queue all go. */
  async function forget() {
    endConnection();
    session = null;
    vault = null;
    await store.clearAll();
    state = await store.getState();
    tombstones.clear();
    extra.clear();
    thumbs.clear();
    lastApplied = null;
    lastLocalHash = null;
    echoes.clear();
    setStatus('signedOut');
  }

  // ---- the history cache ----

  async function upsert(wire) {
    const dead = tombstones.get(wire.id);
    const changed = dead !== undefined && dead >= wire.seq ? false : await store.putClips([wire]);
    if (wire.seq > state.highestSeq) await saveState({ highestSeq: wire.seq });
    return changed;
  }

  async function removeClip(id, seq) {
    if (!(tombstones.get(id) >= seq)) tombstones.set(id, seq);
    if (tombstones.size > 1000) tombstones.delete(tombstones.keys().next().value);
    await store.removeClip(id);
    if (seq > state.highestSeq) await saveState({ highestSeq: seq });
  }

  const checkKey = wire => {
    if (wire.keyId && wire.keyId !== vault.keyId) throw new Stop('reset');   // sealed under a newer key: ours is stale
  };

  async function catchUp(g, api, connecting) {
    const held = new Map((await store.getCache()).map(c => [c.id, time(c.lastCopiedAt)]));
    const caught = [];
    while (true) {
      const before = state.highestSeq;
      const page = await api.sync(before);
      if (g !== gen) return;
      const clips = Array.isArray(page?.clips) ? page.clips : [];
      for (const wire of clips) {
        checkKey(wire);
        await upsert(wire);
        caught.push(wire);
      }
      // A truncated page reports deletions only up to its last clip; the last page continues from latestSeq.
      const tombstoneSeq = page.hasMore && clips.length > 0 ? clips.at(-1).seq : page.latestSeq;
      for (const id of page.deletedIds ?? []) await removeClip(id, tombstoneSeq ?? 0);
      if (!page.hasMore && page.latestSeq > state.highestSeq) await saveState({ highestSeq: page.latestSeq });
      if (!page.hasMore || state.highestSeq <= before) break;
    }

    const cache = await store.getCache();
    if (state.lastAppliedAt === null) {
      // The first catch-up after sign-in or unlock is history only; what it brought is the baseline from now on.
      await saveState({ lastAppliedAt: cache.length > 0 ? time(cache[0].lastCopiedAt) : 0 });
      return;
    }
    const afterPause = connecting && resumingFromPause;
    if (connecting) resumingFromPause = false;
    if (afterPause || state.paused) return;

    const present = new Map(cache.map(c => [c.id, c.seq]));
    const newest = caught
      .filter(c => c.sourceDeviceId !== session.deviceId && present.get(c.id) === c.seq
        && !(held.has(c.id) && time(c.lastCopiedAt) <= held.get(c.id)))
      .reduce((best, c) => (!best || time(c.lastCopiedAt) > time(best.lastCopiedAt)
        || (time(c.lastCopiedAt) === time(best.lastCopiedAt) && c.seq > best.seq) ? c : best), null);
    if (!newest) return;
    if ((await store.queue()).length > 0) return;
    if (cache[0].id !== newest.id || cache[0].seq !== newest.seq) return;
    if (!isNewer(time(newest.lastCopiedAt))) return;
    await applyRemote(newest);
  }

  /** Newer than the last applied clip and than the last local copy: a fresh local copy beats an older incoming clip. */
  const isNewer = at => !(state.lastAppliedAt !== null && at <= state.lastAppliedAt)
    && !(state.lastLocalAt !== null && at <= state.lastLocalAt);

  async function handleMessage(g, msg) {
    if (g !== gen) return;
    switch (msg?.type) {
      case 'vault.reset':
        throw new Stop('reset');
      case 'clip.created': {
        const wire = msg.clip;
        if (!wire || typeof wire.id !== 'string') return;
        checkKey(wire);
        await upsert(wire);
        if (wire.sourceDeviceId === session.deviceId || msg.live !== true || state.paused) return;
        if (lastApplied?.id === wire.id && lastApplied.seq === wire.seq) return;
        if (!isNewer(time(wire.lastCopiedAt))) return;   // an older clip, or a newer local copy
        await applyRemote(wire);
        return;
      }
      case 'clip.deleted':
        if (typeof msg.id === 'string') {
          extra.delete(msg.id);
          await removeClip(msg.id, msg.seq ?? 0);
        }
        return;
      default:
        return;
    }
  }

  // ---- the clipboard ----

  function rememberEcho(hash) {
    const now = clock.now();
    for (const [h, at] of echoes) if (now - at > ECHO_WINDOW_MS) echoes.delete(h);
    echoes.set(hash, now);
  }

  const isEcho = hash => echoes.has(hash) && clock.now() - echoes.get(hash) <= ECHO_WINDOW_MS;

  /** Writes text to the clipboard as this engine's own write: the poll reading it back is not a copy. */
  async function writeOwnText(text) {
    const hash = await contentHashText(text);
    const previous = lastLocalHash;
    rememberEcho(hash);
    lastLocalHash = hash;
    let written = false;
    try { written = await clipboard.writeText(text); } catch { written = false; }
    if (!written) {
      echoes.delete(hash);
      lastLocalHash = previous;
    }
    return written;
  }

  /** Puts a clip from another device on the clipboard: text and links only (see the header). */
  async function applyRemote(wire) {
    const decoded = await decodeClip(wire, vault);
    if (decoded.kind !== 'text' && decoded.kind !== 'link') return false;
    if (state.paused) return false;
    if (!(await writeOwnText(decoded.text))) return false;
    lastApplied = { id: wire.id, seq: wire.seq };
    const at = time(wire.lastCopiedAt);
    if (state.lastAppliedAt === null || at > state.lastAppliedAt) await saveState({ lastAppliedAt: at });
    return true;
  }

  async function captureLocal(item) {
    if (!running || !session || !vault || state.paused) return;
    if (!['syncing', 'offline'].includes(current.state)) return;
    const prepared = await inspectLocal(item, limits(), kinds());
    if (prepared.skip) {
      // Remembered, so the same content read again is neither checked nor reported again.
      if (!prepared.fingerprint || prepared.fingerprint === lastLocalHash) return;
      lastLocalHash = prepared.fingerprint;
      if (prepared.message) notify(prepared.message);
      return;
    }
    const hash = prepared.contentHash;
    if (hash === lastLocalHash) return;
    if (isEcho(hash)) {
      lastLocalHash = hash;
      return;
    }

    const copiedAt = clock.now();
    lastLocalHash = hash;
    try {
      const stored = await currentApi().upload(await sealLocal(prepared, vault, { copiedAt, backfill: false }));
      const at = time(stored?.lastCopiedAt);
      if (state.lastLocalAt === null || at > state.lastLocalAt) await saveState({ lastLocalAt: at });
      if (stored?.id) await store.putClips([stored]);
    } catch (e) {
      if (stopOf(e)) throw e;
      if (isTransient(e)) {
        // Offline: kept sealed under the vault key and sent later as a backfill with its copy time.
        const bytes = prepared.kind === 'image' ? prepared.png : utf8.encode(prepared.text);
        await store.enqueue({ copiedAt, kind: prepared.kind === 'image' ? 'image' : 'text', sealed: await encryptBlob(vault, bytes) });
        if (state.lastLocalAt === null || copiedAt > state.lastLocalAt) await saveState({ lastLocalAt: copiedAt });
      } else if (e instanceof ApiError) {
        notify(e.status === 413 && e.detail ? e.detail : `Server rejected the clip (${e.status}).`);
      }
    }
  }

  async function flushQueue(g, api) {
    for (const item of await store.queue()) {
      if (g !== gen || state.paused || !vault) return;
      let content;
      try {
        const bytes = await decryptBlob(vault, item.sealed);
        content = item.kind === 'image' ? { png: bytes } : { text: fromUtf8.decode(bytes) };
      } catch {
        await store.dequeue(item.id);   // sealed under a key that is gone: nothing to send
        continue;
      }
      const prepared = await inspectLocal(content, limits(), kinds());
      if (prepared.skip) {
        await store.dequeue(item.id);
        if (prepared.message) notify(prepared.message);
        continue;
      }
      try {
        const stored = await api.upload(await sealLocal(prepared, vault, { copiedAt: item.copiedAt, backfill: true }));
        await store.dequeue(item.id);
        if (stored?.id) await store.putClips([stored]);
      } catch (e) {
        if (stopOf(e)) throw e;   // a reset relocks, which drops the queue
        if (e instanceof ApiError && e.status === 410) {
          await store.dequeue(item.id);   // cleared, or past the auto-delete age: dropped quietly
        } else if (isTransient(e)) {
          const attempts = (item.attempts ?? 0) + 1;
          if (attempts >= DROP_AFTER_ATTEMPTS) {
            await store.dequeue(item.id);
            notify(MESSAGES.syncFailed);
            continue;
          }
          await store.updateQueued({ ...item, attempts });
          return;   // the rest waits for the next flush, in order
        } else {
          await store.dequeue(item.id);
          notify(`Server rejected a queued clip (${e instanceof ApiError ? e.status : 0}); it was dropped.`);
        }
      }
    }
  }

  // ---- history for the popup ----

  async function thumbFor(wire) {
    if (!wire.hasThumb) return undefined;
    const key = `${wire.id}:${wire.hash}`;
    if (thumbs.has(key)) return thumbs.get(key);
    try {
      const sealed = await currentApi().thumb(wire.id);
      const png = sealed ? await openSmall(vault, 'thumb', sealed) : undefined;
      if (png) {
        if (thumbs.size >= 600) thumbs.delete(thumbs.keys().next().value);
        thumbs.set(key, png);
      }
      return png;
    } catch {
      return undefined;   // shown without a thumbnail; asked again next time
    }
  }

  async function toRows(clips, query) {
    const q = (query ?? '').trim().toLowerCase();
    const rows = [];
    for (const wire of clips) {
      const decoded = await decodeClip(wire, vault);
      if (q && !((decoded.text ?? '').toLowerCase().includes(q)
        || (decoded.files ?? []).some(f => f.name.toLowerCase().includes(q)))) continue;
      rows.push({
        id: wire.id,
        ...decoded,
        thumbPng: decoded.kind === 'image' ? await thumbFor(wire) : undefined,
        sourceDeviceName: wire.sourceDeviceName ?? null,
        sourceDevicePlatform: wire.sourceDevicePlatform ?? null,
        sourceUserName: wire.sourceUserName ?? null,
        lastCopiedAt: wire.lastCopiedAt,
        pinnedAt: wire.pinnedAt ?? null,
      });
    }
    return rows;
  }

  async function findClip(id) {
    return (await store.getCache()).find(c => c.id === id) ?? extra.get(id) ?? null;
  }

  // ---- the public interface ----

  return {
    /** Loads the stored session and vault and, when both are there, starts syncing. */
    async start() {
      running = true;
      session = await store.getSession();
      vault = await store.getVault();
      state = await store.getState();
      if (!session) return setStatus('signedOut');
      if (!vault) return setStatus(state.lockState ?? 'locked', state.lockMessage ?? undefined);
      if (state.paused) return setStatus('paused');
      launch();
    },

    stop() {
      running = false;
      endConnection();
    },

    /**
     * Signs in as a new device (platform extension). Afterwards the status is `locked` (unlock next) or `noVault`.
     * @throws {ApiError} with the server's detail as message (e.g. 'Wrong name or password.'); Error for a server too old.
     */
    async signIn(serverUrl, username, password, deviceName) {
      const url = String(serverUrl ?? '').trim().replace(/\/+$/, '');
      if (!/^https?:\/\/[^/]+/i.test(url)) throw new Error('Enter the server address, starting with https://');
      const anonymous = makeApi({ serverUrl: url });
      let info;
      try {
        info = await anonymous.serverInfo();
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) throw new Error(MESSAGES.serverTooOld);
        throw e;
      }
      const name = String(deviceName ?? '').trim() || DEFAULT_DEVICE_NAME;
      const { deviceId, token } = await anonymous.login(username, password, name);
      await forget();
      session = { serverUrl: url, token, deviceId, deviceName: name };
      await store.setSession(session);
      await saveState({ serverInfo: readServerInfo(info) });
      running = true;
      const wrap = await currentApi().vault();
      if (!wrap) await handleStop('noVault');
      else setStatus('locked');
      return status();
    },

    /**
     * Unlocks with the passphrase and starts syncing. The vault is stored as non-extractable keys; the raw key is wiped.
     * @throws {Error} with the message to show (a wrong passphrase, ...), or ApiError.
     */
    async unlock(passphrase) {
      if (!session) throw new Error(MESSAGES.signInFirst);
      const api = currentApi();
      let wrap;
      try {
        wrap = await api.vault();
      } catch (e) {
        const stop = stopOf(e);
        if (stop === 'revoked' || stop === 'appTooOld') await handleStop(stop);
        throw e;
      }
      if (!wrap) {
        await handleStop('noVault');
        return status();
      }
      let unwrapped;
      try {
        unwrapped = await unwrapWithPassphrase(wrap, passphrase);
      } catch (e) {
        throw new Error(e instanceof WrongPassphraseError ? MESSAGES.wrongPassphrase : MESSAGES.unsupportedWrap);
      }
      if (vault && vault.keyId !== unwrapped.keyId) await relock('locked');
      await store.setVault(unwrapped);   // wipes unwrapped.raw
      vault = await store.getVault();
      await saveState({ lockState: null, lockMessage: null });
      running = true;
      if (state.paused) setStatus('paused');
      else launch();
      return status();
    },

    /** Signs out: revokes this device's token on the server (best effort) and deletes everything stored. */
    async signOut() {
      const s = session;
      endConnection();
      if (s) {
        try { await makeApi({ serverUrl: s.serverUrl, token: s.token }).logout(); } catch { /* offline: still forgotten here */ }
      }
      await forget();
      return status();
    },

    /** Pausing disconnects: nothing is uploaded or applied. The first catch-up after resuming is history only. */
    async setPaused(paused) {
      await saveState({ paused: !!paused });
      if (paused) {
        endConnection();
        if (session && vault) setStatus('paused');
        return status();
      }
      resumingFromPause = true;
      if (running && session && vault) launch();
      return status();
    },

    /** A change the clipboard poller saw: {text} or {png}. Copies are handled one at a time, in order. */
    onLocalChange(item) {
      return serial(() => captureLocal(item)).catch(e => {
        const stop = stopOf(e);
        if (stop) return handleStop(stop);
      });
    },

    /**
     * Decrypted history rows for the popup. Without a cursor: the local cache (newest first, or pinned ones most recently
     * pinned first), with `nextCursor` pointing at the server's history for "Search older clips"; with that cursor: a
     * page of older clips from the server, without the ones the cache already showed. `query` matches text and file
     * names, case-insensitively. Rows: {id, kind ('text'|'link'|'image'|'file'|'unknown'), text?, files?, width?,
     * height?, thumbPng?, sourceDeviceName, sourceDevicePlatform, sourceUserName, lastCopiedAt, pinnedAt}.
     */
    async history({ pinned = false, cursor = null, query = '' } = {}) {
      if (!session || !vault) return { rows: [], nextCursor: null };
      const cache = await store.getCache();
      if (!cursor) {
        const clips = pinned
          ? cache.filter(c => c.pinnedAt).sort((a, b) => time(b.pinnedAt) - time(a.pinnedAt) || b.seq - a.seq)
          : cache;
        return { rows: await toRows(clips, query), nextCursor: SERVER_CURSOR };
      }
      if (!cursor.startsWith(SERVER_CURSOR)) throw new Error('Unknown history cursor.');
      const page = await currentApi().clips({ cursor: cursor.slice(SERVER_CURSOR.length) || undefined, limit: 50, pinned });
      const cached = new Set(cache.map(c => c.id));
      const older = (page?.items ?? []).filter(c => !cached.has(c.id));
      for (const c of older) {
        extra.set(c.id, c);
        if (extra.size > 1000) extra.delete(extra.keys().next().value);
      }
      return { rows: await toRows(older, query), nextCursor: page?.nextCursor ? SERVER_CURSOR + page.nextCursor : null };
    },

    /**
     * Puts a clip on the clipboard. True for text and links once written. False for an image (the extension can't
     * write one from the background: the popup writes it, after download(id, 0); its hash is remembered here so the
     * poll reading it back isn't uploaded again), and for files and unknown clips, which never go on the clipboard.
     */
    async copy(id) {
      if (!vault) return false;
      const wire = await findClip(id);
      if (!wire) return false;
      const decoded = await decodeClip(wire, vault);
      if (decoded.kind === 'text' || decoded.kind === 'link') return serial(() => writeOwnText(decoded.text));
      if (decoded.kind === 'image') {
        try {
          const png = await decryptBlob(vault, await currentApi().blob(id, 0));
          const { rgba, width, height } = await pngToRgba(png);
          const hash = await contentHashPixels(rgba, width, height);
          await serial(() => { rememberEcho(hash); lastLocalHash = hash; });
        } catch { /* the popup's own write still works; it may come back as a new copy */ }
      }
      return false;
    },

    /**
     * The decrypted bytes and safe name of file `n` of a file clip, or of an image clip's PNG (n = 0).
     * @returns {Promise<{bytes: Uint8Array, name: string, mime?: string}>}
     */
    async download(id, n = 0) {
      if (!vault) throw new Error(MESSAGES.signInFirst);
      const wire = await findClip(id);
      if (!wire) throw new Error('That clip is gone.');
      const decoded = await decodeClip(wire, vault);
      if (decoded.kind === 'file' && Number.isInteger(n) && n >= 0 && n < decoded.files.length) {
        const bytes = await decryptBlob(vault, await currentApi().blob(id, n));
        return { bytes, name: decoded.files[n].name, mime: decoded.files[n].mime };
      }
      if (decoded.kind === 'image' && n === 0) {
        return { bytes: await decryptBlob(vault, await currentApi().blob(id, 0)), name: 'image.png', mime: 'image/png' };
      }
      throw new Error('That clip has nothing to download.');
    },

    status,
  };
}
