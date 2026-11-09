// The PrivatePaste REST API (protocol 2, docs/api.md). Every request carries the protocol header and, once signed in, the
// device's bearer token. Never log a token, a password or what a clip holds.

export const PROTOCOL_HEADER = 'X-PrivatePaste-Protocol';
export const PROTOCOL_VERSION = '2';
export const APP_VERSION_HEADER = 'X-PrivatePaste-App-Version';
export const PLATFORM = 'extension';

/** A non-success answer from the server: its status and, for a problem response, its title and detail. */
export class ApiError extends Error {
  constructor(status, title = null, detail = null) {
    super(detail ?? title ?? `The server answered ${status}.`);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
  }
}

/** A failure worth trying again later: no answer at all (offline), a 5xx, 408 or 429. */
export const isTransient = e =>
  !(e instanceof ApiError) || e.status >= 500 || e.status === 408 || e.status === 429;

/**
 * @param {{serverUrl: string, token?: string, fetch: typeof fetch, appVersion?: string}} options
 *   appVersion: the extension's version, sent on every call so the server can show which version each device runs.
 */
export function createApi({ serverUrl, token, fetch, appVersion }) {
  const base = serverUrl.replace(/\/+$/, '');
  const path = id => encodeURIComponent(id);

  async function send(method, url, { body, json } = {}) {
    const headers = { [PROTOCOL_HEADER]: PROTOCOL_VERSION };
    if (appVersion) headers[APP_VERSION_HEADER] = appVersion;
    if (token) headers.Authorization = `Bearer ${token}`;
    if (json !== undefined) {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(json);
    }
    const response = await fetch(base + url, { method, headers, body, cache: 'no-store', credentials: 'omit' });
    if (!response.ok) {
      let title = null, detail = null;
      try {
        const problem = await response.json();
        title = typeof problem?.title === 'string' ? problem.title : null;
        detail = typeof problem?.detail === 'string' ? problem.detail : null;
      } catch { /* not a problem response */ }
      throw new ApiError(response.status, title, detail);
    }
    return response;
  }

  const getJson = async (url) => (await send('GET', url)).json();
  const bytes = async (url) => new Uint8Array(await (await send('GET', url)).arrayBuffer());
  const query = params => {
    const q = new URLSearchParams();
    for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') q.set(k, String(v));
    const s = q.toString();
    return s ? `?${s}` : '';
  };

  return {
    serverUrl: base,
    /** The WebSocket address; the socket must also carry the token and the protocol header (see engine-host.js). */
    socketUrl: () => base.replace(/^http/i, 'ws') + '/ws',
    serverInfo: () => getJson('/api/server-info'),
    login: async (username, password, deviceName) =>
      (await send('POST', '/api/auth/login', { json: { username, password, deviceName, platform: PLATFORM } })).json(),
    logout: async () => { await send('POST', '/api/auth/logout'); },
    me: () => getJson('/api/me'),
    /** The vault wrap, or null when encryption isn't set up yet (404). */
    vault: async () => {
      try {
        return await getJson('/api/vault');
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) return null;
        throw e;
      }
    },
    clips: ({ cursor, limit, pinned } = {}) => getJson('/api/clips' + query({ cursor, limit, pinned: pinned ? 'true' : undefined })),
    sync: (afterSeq, limit) => getJson('/api/sync' + query({ afterSeq, limit })),
    upload: async ({ meta, thumb, blobs = [] }) => {
      const form = new FormData();
      form.append('meta', new Blob([JSON.stringify(meta)], { type: 'application/json' }), 'meta.json');
      if (thumb) form.append('thumb', new Blob([thumb]), 'thumb');
      blobs.forEach((b, n) => form.append(`blob${n}`, new Blob([b]), `blob${n}`));
      return (await send('POST', '/api/clips', { body: form })).json();
    },
    blob: (id, n) => bytes(`/api/clips/${path(id)}/blobs/${n}`),
    /** The sealed thumbnail, or null when the clip has none (404). */
    thumb: async (id) => {
      try {
        return await bytes(`/api/clips/${path(id)}/thumb`);
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) return null;
        throw e;
      }
    },
    pin: async (id) => (await send('POST', `/api/clips/${path(id)}/pin`)).json(),
    unpin: async (id) => (await send('DELETE', `/api/clips/${path(id)}/pin`)).json(),
    remove: async (id) => { await send('DELETE', `/api/clips/${path(id)}`); },
  };
}
