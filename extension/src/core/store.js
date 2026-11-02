// The extension's persistent state, in IndexedDB: the device session, the vault keys, the sync state, the history cache
// (clips as the server sent them, still sealed) and the offline queue (sealed under the vault key). Nothing here is
// plaintext content, and the vault is kept only as non-extractable CryptoKeys, never as raw key bytes.

import { importVaultKey } from '../js/e2e.js';

/** The desktop's history size; pinned clips are kept beyond it so the Pinned tab shows every pin. */
export const CACHE_CAPACITY = 500;

const DEFAULT_STATE = Object.freeze({
  highestSeq: 0, lastAppliedAt: null, lastLocalAt: null, paused: false, serverInfo: null,
});

const time = iso => Date.parse(iso) || 0;
const newestFirst = (a, b) => time(b.lastCopiedAt) - time(a.lastCopiedAt) || b.seq - a.seq;

/** An in-memory backend with IndexedDB's copy semantics, for tests. */
export function memoryBackend() {
  const map = new Map();
  return {
    get: async key => (map.has(key) ? structuredClone(map.get(key)) : undefined),
    set: async (key, value) => { map.set(key, structuredClone(value)); },
    delete: async key => { map.delete(key); },
    entries: async prefix => [...map.entries()].filter(([k]) => k.startsWith(prefix)).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
      .map(([k, v]) => [k, structuredClone(v)]),
    clear: async () => { map.clear(); },
  };
}

/** A key-value backend over one IndexedDB object store. */
export function indexedDbBackend(indexedDB, name = 'privatepaste') {
  const opened = new Promise((resolve, reject) => {
    const request = indexedDB.open(name, 1);
    request.onupgradeneeded = () => request.result.createObjectStore('kv');
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
  // `work` returns one request, or an array of them read in the same transaction.
  const run = async (mode, work) => {
    const db = await opened;
    return new Promise((resolve, reject) => {
      const tx = db.transaction('kv', mode);
      const request = work(tx.objectStore('kv'));
      tx.oncomplete = () => resolve(Array.isArray(request) ? request.map(r => r.result) : request?.result);
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  };
  const LAST = String.fromCharCode(0xffff);
  return {
    get: key => run('readonly', s => s.get(key)),
    set: (key, value) => run('readwrite', s => s.put(value, key)).then(() => undefined),
    delete: key => run('readwrite', s => s.delete(key)).then(() => undefined),
    entries: async prefix => {
      const range = IDBKeyRange.bound(prefix, prefix + LAST);
      // One transaction, so a write can't land between the keys and the values.
      const [keys, values] = await run('readonly', s => [s.getAllKeys(range), s.getAll(range)]);
      return keys.map((k, i) => [k, values[i]]);
    },
    clear: () => run('readwrite', s => s.clear()).then(() => undefined),
  };
}

/**
 * @param backend an IDBFactory (the browser's indexedDB) or a key-value backend such as memoryBackend().
 */
export function openStore(backend) {
  const kv = typeof backend?.open === 'function' ? indexedDbBackend(backend) : backend;
  let cache = null;          // [clip], newest first; loaded on first use
  let stateWrites = Promise.resolve();   // state updates one after another, so none is lost
  let queueCounter = null;

  const loadCache = async () => {
    cache ??= (await kv.entries('clip:')).map(([, clip]) => clip).sort(newestFirst);
    return cache;
  };

  return {
    getSession: async () => (await kv.get('session')) ?? null,
    setSession: async ({ serverUrl, token, deviceId, deviceName }) => kv.set('session', { serverUrl, token, deviceId, deviceName }),

    /** {keyId, encKey, hashKey} as non-extractable CryptoKeys, or null. */
    getVault: async () => (await kv.get('vault')) ?? null,
    /**
     * Stores the vault as non-extractable keys. Given the raw key (`vault.raw`, from unwrapping), re-derives the subkeys
     * with extractable false and wipes the raw bytes, so they never reach the disk.
     */
    setVault: async vault => {
      if (!vault) return kv.delete('vault');
      let keys = vault;
      if (vault.raw instanceof Uint8Array) {
        try {
          keys = await importVaultKey(vault.raw);
          keys.raw.fill(0);
        } finally {
          vault.raw.fill(0);
        }
      }
      if (keys.encKey.extractable || keys.hashKey.extractable) throw new Error('The vault keys must not be extractable.');
      return kv.set('vault', { keyId: keys.keyId, encKey: keys.encKey, hashKey: keys.hashKey });
    },

    getState: async () => ({ ...DEFAULT_STATE, ...((await kv.get('state')) ?? {}) }),
    /** Merges the given fields into the stored state. */
    setState: partial => {
      const write = stateWrites.then(async () => {
        const next = { ...DEFAULT_STATE, ...((await kv.get('state')) ?? {}), ...partial };
        await kv.set('state', next);
        return next;
      });
      stateWrites = write.catch(() => {});
      return write;
    },

    /** The cached clips, newest first (lastCopiedAt, then seq). */
    getCache: async () => [...(await loadCache())],
    /**
     * Adds or replaces clips; a clip older than (or as old as) the held copy is ignored. Keeps the newest
     * CACHE_CAPACITY clips, plus every pinned one. Returns whether anything changed.
     */
    putClips: async clips => {
      const list = await loadCache();
      const changed = [];
      for (const clip of clips) {
        const at = list.findIndex(c => c.id === clip.id);
        if (at >= 0 && list[at].seq >= clip.seq) continue;
        if (at >= 0) list.splice(at, 1);
        list.push(clip);
        changed.push(clip);
      }
      if (changed.length === 0) return false;
      list.sort(newestFirst);
      let dropped = [];
      if (list.length > CACHE_CAPACITY) {
        const keep = [...list.slice(0, CACHE_CAPACITY), ...list.slice(CACHE_CAPACITY).filter(c => c.pinnedAt)];
        dropped = list.filter(c => !keep.includes(c));
        list.splice(0, list.length, ...keep);
      }
      for (const clip of changed) if (!dropped.includes(clip)) await kv.set(`clip:${clip.id}`, clip);
      for (const clip of dropped) await kv.delete(`clip:${clip.id}`);
      return true;
    },
    removeClip: async id => {
      const list = await loadCache();
      const at = list.findIndex(c => c.id === id);
      if (at < 0) return false;
      list.splice(at, 1);
      await kv.delete(`clip:${id}`);
      return true;
    },
    clearCache: async () => {
      for (const [key] of await kv.entries('clip:')) await kv.delete(key);
      cache = [];
    },

    /** Queued copies, oldest first: {id, copiedAt, kind, sealed, attempts}. */
    queue: async () => (await kv.entries('queue:')).map(([, item]) => item),
    enqueue: async item => {
      if (queueCounter === null) {
        const last = (await kv.entries('queue:')).at(-1);
        queueCounter = last ? Number(last[0].slice(6)) : 0;
      }
      const id = String(++queueCounter).padStart(12, '0');
      const stored = { attempts: 0, ...item, id };
      await kv.set(`queue:${id}`, stored);
      return stored;
    },
    updateQueued: async item => kv.set(`queue:${item.id}`, item),
    dequeue: async id => kv.delete(`queue:${id}`),
    clearQueue: async () => {
      for (const [key] of await kv.entries('queue:')) await kv.delete(key);
    },

    /** Deletes everything: token, vault keys, state, history cache and queue (sign-out or revoke). */
    clearAll: async () => {
      await kv.clear();
      cache = [];
      queueCounter = null;
    },
  };
}
