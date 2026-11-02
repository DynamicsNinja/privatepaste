// The server address rule the desktop and Android apps follow (Client.Core ServerUrl): https://, or plain http:// only
// for this computer. Over plain http to anywhere else the login password and the device token would cross the network
// in the clear.

export const INVALID_URL = 'Enter a full server address starting with https://';
export const HTTP_NOT_LOCAL = 'The server address must use https:// (http:// is only allowed for localhost).';

const LOOPBACK = new Set(['localhost', '127.0.0.1', '[::1]']);

/**
 * The address as the engine keeps it (trimmed, without trailing slashes).
 * @throws {Error} with the text to show, for anything but https:// or a loopback http:// address.
 */
export function normalizeServerUrl(input) {
  const text = String(input ?? '').trim().replace(/\/+$/, '');
  let url;
  try {
    url = new URL(text);
  } catch {
    throw new Error(INVALID_URL);
  }
  if ((url.protocol !== 'https:' && url.protocol !== 'http:') || !url.hostname || !/^https?:\/\//i.test(text)) {
    throw new Error(INVALID_URL);
  }
  if (url.protocol === 'http:' && !LOOPBACK.has(url.hostname)) throw new Error(HTTP_NOT_LOCAL);
  return text;
}
