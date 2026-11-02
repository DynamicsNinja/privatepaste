// Fakes for the engine tests: a scripted server behind the api interface, a WebSocket, a clipboard and a clock. Nothing
// here touches the network or the real clipboard.
import { ApiError } from '../../src/core/api.js';

/** Waits for pending promises, crypto included, to run. */
export const settle = (ms = 15) => new Promise(resolve => setTimeout(resolve, ms));

export async function until(predicate, what = 'condition', timeoutMs = 3000) {
  const end = Date.now() + timeoutMs;
  while (!(await predicate())) {
    if (Date.now() > end) throw new Error(`Timed out waiting for ${what}.`);
    await settle(5);
  }
}

export function fakeClock(start = Date.parse('2026-11-02T10:00:00Z')) {
  let now = start;
  let timers = [];
  let nextId = 1;
  return {
    now: () => now,
    setTimeout(fn, ms) {
      const id = nextId++;
      timers.push({ id, at: now + ms, fn });
      return id;
    },
    clearTimeout(id) { timers = timers.filter(t => t.id !== id); },
    /** Moves time forward, firing due timers in order and letting their work run. */
    async advance(ms) {
      const end = now + ms;
      while (true) {
        timers.sort((a, b) => a.at - b.at);
        const next = timers[0];
        if (!next || next.at > end) break;
        timers.shift();
        now = next.at;
        next.fn();
        await settle();
      }
      now = end;
      await settle();
    },
  };
}

export function fakeClipboard() {
  return {
    written: [],
    images: [],
    writeOk: true,
    async writeText(text) { if (!this.writeOk) return false; this.written.push(text); return true; },
    async writeImage(png) { this.images.push(png); return false; },
    async readClipboard() { return null; },
  };
}

export function fakeSockets() {
  const sockets = [];
  const control = { refuse: false };
  class FakeWebSocket {
    constructor(url) {
      this.url = url;
      this.sent = [];
      this.readyState = 0;
      sockets.push(this);
      setImmediate(() => {
        if (this.readyState !== 0) return;
        if (control.refuse) {
          this.readyState = 3;
          this.onerror?.({});
          this.onclose?.({ code: 1006 });
        } else {
          this.readyState = 1;
          this.onopen?.({});
        }
      });
    }
    send(data) { this.sent.push(JSON.parse(data)); }
    close(code = 1000) {
      if (this.readyState === 3) return;
      this.readyState = 3;
      setImmediate(() => this.onclose?.({ code }));
    }
    /** A message from the server. */
    receive(msg) { this.onmessage?.({ data: JSON.stringify(msg) }); }
  }
  return { FakeWebSocket, sockets, control, open: () => sockets.filter(s => s.readyState === 1) };
}

/**
 * A scripted server. `api` is the factory the engine takes. Uploads are stored and answered like the real server, with
 * `now()` as the server time, unless `uploadError` (thrown once per entry, in order) says otherwise.
 */
export function fakeServer({ wrap, now }) {
  const server = {
    calls: [],
    uploads: [],
    tokens: new Set(),
    info: { protocol: 2, kinds: ['text', 'link', 'image', 'file'], limits: { textKb: 1024, imageMb: 20, fileMb: 25 } },
    infoError: null,
    wrap,
    vaultError: null,
    isOwner: true,
    syncPages: [],
    syncError: null,
    latestSeq: 0,
    uploadErrors: [],
    stored: [],
    blobs: new Map(),
    thumbs: new Map(),
    pages: [],
    seq: 100,
    now,
    record(name, ...args) { this.calls.push({ name, args }); },
    count(name) { return this.calls.filter(c => c.name === name).length; },
  };
  server.api = ({ serverUrl, token }) => {
    if (token) server.tokens.add(token);
    return {
      serverUrl,
      socketUrl: () => serverUrl.replace(/^http/, 'ws') + '/ws',
      async serverInfo() {
        server.record('serverInfo');
        if (server.infoError) throw server.infoError;
        return server.info;
      },
      async login(username, password, deviceName) {
        server.record('login', username, deviceName);
        if (password !== 'dev') throw new ApiError(401, 'Unauthorized', 'Wrong name or password.');
        return { deviceId: 'dev-own', token: 'tok-new' };
      },
      async logout() { server.record('logout'); },
      async me() { server.record('me'); return { name: 'someone', isOwner: server.isOwner }; },
      async vault() {
        server.record('vault');
        if (server.vaultError) throw server.vaultError;
        return server.wrap;
      },
      async sync(afterSeq) {
        server.record('sync', afterSeq);
        if (server.syncError) throw server.syncError;
        return server.syncPages.shift() ?? { clips: [], deletedIds: [], latestSeq: Math.max(server.latestSeq, afterSeq), hasMore: false };
      },
      async upload(upload) {
        server.record('upload');
        server.uploads.push(upload);
        const error = server.uploadErrors.shift();
        if (error) throw error;
        const clip = {
          id: `up${server.uploads.length}`,
          seq: ++server.seq,
          kind: upload.meta.kind,
          keyId: upload.meta.keyId,
          hash: upload.meta.hash,
          payload: upload.meta.payload,
          sizeBytes: upload.meta.sizeBytes,
          sourceDeviceId: 'dev-own',
          sourceDeviceName: 'Edge',
          sourceDevicePlatform: 'extension',
          sourceUserName: null,
          lastCopiedAt: new Date(upload.meta.backfill ? Date.parse(upload.meta.copiedAt) : server.now()).toISOString(),
          pinnedAt: null,
          hasThumb: !!upload.thumb,
          blobCount: upload.blobs.length,
        };
        server.stored.push(clip);
        return clip;
      },
      async clips(options) {
        server.record('clips', options);
        return server.pages.shift() ?? { items: [], nextCursor: null };
      },
      async blob(id, n) {
        server.record('blob', id, n);
        const b = server.blobs.get(`${id}/${n}`);
        if (!b) throw new ApiError(404, 'Not Found');
        return b;
      },
      async thumb(id) { server.record('thumb', id); return server.thumbs.get(id) ?? null; },
      async pin(id) { server.record('pin', id); },
      async unpin(id) { server.record('unpin', id); },
      async remove(id) { server.record('remove', id); },
    };
  };
  return server;
}
