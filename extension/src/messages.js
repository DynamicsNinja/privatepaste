// The engine's side of the popup's runtime messages, answered by the offscreen document (offscreen.js). The popup holds
// no keys: it asks here, and the engine decrypts. Every answer is JSON, {ok: true, value} or {ok: false, error} with the
// text to show, so an error crosses the message channel; thumbnails go as base64. Never put clip content in an error.
//
//   {type: 'engine.status'}                                        -> status()
//   {type: 'engine.signIn', serverUrl, username, password, deviceName} -> status()
//   {type: 'engine.unlock', passphrase}                            -> status()
//   {type: 'engine.signOut'}                                       -> status()
//   {type: 'engine.setPaused', paused}                             -> status()
//   {type: 'engine.history', pinned, cursor, query, thumbs = true} -> {rows, nextCursor}; image rows carry
//                                                                     thumbPngBase64, or only hasThumb with thumbs false
//   {type: 'engine.copy', id}                                      -> true once written; false for an image (the popup
//                                                                     writes it), a file or an unknown clip
//   {type: 'engine.download', id, n}                               -> {url, name, mime?}: a blob URL of this page,
//                                                                     revoked on engine.release or after a minute
//   {type: 'engine.release', url}                                  -> true
//
// Downloads don't cross as base64: a file can be bigger than a runtime message may be (64 MiB), the server's file
// limit being configurable. The blob URL belongs to this page, which stays open, so a save dialog that closes the popup
// doesn't cancel the download either.

import { toBase64 } from './core/base64.js';

const UNREACHABLE = "Couldn't reach the server. Check the address.";
const FAILED = 'Something went wrong. Try again.';
export const URL_LIFETIME_MS = 60000;

/** What a blob URL is made and revoked with; injectable for the tests. */
const browserUrls = {
  create: blob => URL.createObjectURL(blob),
  revoke: url => URL.revokeObjectURL(url),
  setTimeout: (fn, ms) => setTimeout(fn, ms),
};

function historyAnswer({ rows, nextCursor }, thumbs) {
  return {
    nextCursor: nextCursor ?? null,
    rows: rows.map(({ thumbPng, ...row }) => {
      if (thumbPng && thumbs) row.thumbPngBase64 = toBase64(thumbPng);
      else if (thumbPng) row.hasThumb = true;
      return row;
    }),
  };
}

const handlers = {
  'engine.status': engine => engine.status(),
  'engine.signIn': (engine, m) => engine.signIn(m.serverUrl, m.username, m.password, m.deviceName),
  'engine.unlock': (engine, m) => engine.unlock(m.passphrase),
  'engine.signOut': engine => engine.signOut(),
  'engine.setPaused': (engine, m) => engine.setPaused(!!m.paused),
  'engine.history': async (engine, m) =>
    historyAnswer(await engine.history({ pinned: !!m.pinned, cursor: m.cursor ?? null, query: m.query ?? '' }), m.thumbs !== false),
  'engine.copy': async (engine, m) => !!(await engine.copy(m.id)),
  'engine.download': async (engine, m, urls) => {
    const { bytes, name, mime } = await engine.download(m.id, m.n ?? 0);
    // application/octet-stream, so the browser never opens or runs a file, whatever its name or the sender's mime.
    const url = urls.create(new Blob([bytes], { type: 'application/octet-stream' }));
    urls.issued.add(url);
    urls.setTimeout(() => release(urls, url), URL_LIFETIME_MS);
    return { url, name, mime };
  },
  // Only URLs handed out here; anything else is ignored.
  'engine.release': (_engine, m, urls) => release(urls, m.url),
};

function release(urls, url) {
  if (!urls.issued.delete(url)) return false;
  urls.revoke(url);
  return true;
}

/** Blob URL bookkeeping: create, revoke, setTimeout and the set of URLs still out. */
export const urlKeeper = (impl = browserUrls) => ({ ...impl, issued: new Set() });
const defaultUrls = urlKeeper();

/**
 * Sends an answer, and when that fails (the popup closed, or the answer can't be sent) tries a short failure instead,
 * so the asker never waits forever. The popup never resends on such a failure (ui/ask.js).
 */
export function replyWith(answer, reply) {
  return answer.then(value => {
    try {
      reply(value);
    } catch {
      try { reply({ ok: false, error: FAILED }); } catch { /* nobody to tell */ }
    }
  });
}

/**
 * The answer to a popup message, or undefined when the message isn't one of these (the fake clipboard's test messages
 * and the engine's own status broadcasts share the channel).
 * @returns {Promise<{ok: true, value} | {ok: false, error: string}> | undefined}
 */
export function handleEngineMessage(engine, message, urls = defaultUrls) {
  const handler = Object.hasOwn(handlers, message?.type) ? handlers[message.type] : null;
  if (!handler) return undefined;
  return (async () => {
    try {
      await engine.ready;
      return { ok: true, value: await handler(engine, message, urls) };
    } catch (e) {
      // fetch rejects with a TypeError when the server doesn't answer (or CORS refuses without the host permission);
      // any other TypeError is a bug, whose message means nothing to the owner.
      if (e instanceof TypeError) return { ok: false, error: /fetch|network/i.test(e.message) ? UNREACHABLE : FAILED };
      return { ok: false, error: String(e?.message || FAILED) };
    }
  })();
}
