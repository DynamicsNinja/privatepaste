// Puts the device token and the protocol header on the WebSocket upgrade request, which a page can't do itself: a
// declarativeNetRequest session rule, installed by the service worker (the offscreen document has no access to that
// API). Session rules live only while the browser runs and are never written to disk; the engine asks for the rule
// again before every socket, so it is back after a browser restart, and removes it on sign-out and revoke.
// The rule matches only that one socket URL and only requests that come from no tab (the extension's own documents),
// so a web page opening a socket to the same server doesn't get the token.

export const SET_WS_AUTH = 'sw.setWsAuth';
export const CLEAR_WS_AUTH = 'sw.clearWsAuth';
export const WS_AUTH_RULE_ID = 1;
const TAB_ID_NONE = -1;

/** The session rule for one socket URL (`ws://host/ws` or `wss://host/base/ws`) and token. */
export function wsAuthRule(url, token) {
  return {
    id: WS_AUTH_RULE_ID,
    priority: 1,
    action: {
      type: 'modifyHeaders',
      requestHeaders: [
        { header: 'Authorization', operation: 'set', value: `Bearer ${token}` },
        { header: 'X-PrivatePaste-Protocol', operation: 'set', value: '2' },
      ],
    },
    condition: {
      // urlFilter anchors: '|' at both ends matches the whole URL, nothing before or after it.
      urlFilter: `|${url}|`,
      resourceTypes: ['websocket'],
      tabIds: [TAB_ID_NONE],
    },
  };
}

const validUrl = url => typeof url === 'string' && /^wss?:\/\/[^\s|*^]+\/ws$/.test(url);
const validToken = token => typeof token === 'string' && /^[\x21-\x7e]+$/.test(token);

/**
 * Handles the engine's messages in the service worker. `dnr` is chrome.declarativeNetRequest. Returns a promise of the
 * reply for our messages, undefined for anything else. Only the extension's own documents can send runtime messages.
 */
export function handleWsAuthMessage(message, dnr) {
  if (message?.type === SET_WS_AUTH) {
    if (!validUrl(message.url) || !validToken(message.token)) return Promise.resolve({ ok: false });
    return dnr.updateSessionRules({ removeRuleIds: [WS_AUTH_RULE_ID], addRules: [wsAuthRule(message.url, message.token)] })
      .then(() => ({ ok: true }), () => ({ ok: false }));
  }
  if (message?.type === CLEAR_WS_AUTH) {
    return dnr.updateSessionRules({ removeRuleIds: [WS_AUTH_RULE_ID] }).then(() => ({ ok: true }), () => ({ ok: false }));
  }
  return undefined;
}
