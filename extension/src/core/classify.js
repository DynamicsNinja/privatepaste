// Whether copied text is uploaded as a link or as text: the rule of ContentClassifier.cs (one absolute http or https URL
// as System.Uri parses it, after trimming, with no whitespace inside). docs/classifier-test-vectors.json pins it.

// char.IsWhiteSpace in .NET: unlike JavaScript's \s it includes NEL (U+0085) and excludes the BOM (U+FEFF).
// Written as code points, so no invisible character sits in the source.
const WHITESPACE_RANGES = [[0x09, 0x0d], [0x20], [0x85], [0xa0], [0x1680], [0x2000, 0x200a], [0x2028, 0x2029], [0x202f], [0x205f], [0x3000]];
const WHITESPACE = new RegExp(`[${WHITESPACE_RANGES.map(r => r.map(c => String.fromCharCode(c)).join('-')).join('')}]`);
const LEADING = new RegExp(`^${WHITESPACE.source}+`);
const TRAILING = new RegExp(`${WHITESPACE.source}+$`);

// A DNS host as System.Uri accepts it for http: labels of letters (any script), digits, '-' and '_', separated by single
// dots, an optional trailing dot. Percent escapes and other punctuation make the URI invalid.
const HOST_LABEL = /^[\p{L}\p{N}\p{M}_-]+$/u;

/** @returns {'link' | 'text'} */
export function classifyText(text) {
  const t = text.replace(LEADING, '').replace(TRAILING, '');
  if (t.length === 0 || WHITESPACE.test(t)) return 'text';
  return isHttpUrl(t) ? 'link' : 'text';
}

function isHttpUrl(t) {
  const scheme = /^(https?):\/\//i.exec(t);
  if (!scheme) return false;
  const rest = t.slice(scheme[0].length);
  // The authority ends at the first path, query or fragment delimiter (System.Uri reads '\' as '/').
  const end = rest.search(/[/\\?#]/);
  const authority = end < 0 ? rest : rest.slice(0, end);

  const at = authority.lastIndexOf('@');
  if (at >= 0 && authority.indexOf('@') !== at) return false;   // a second '@'
  const hostPort = authority.slice(at + 1);
  if (hostPort.length === 0) return false;

  let host, port;
  if (hostPort.startsWith('[')) {
    const close = hostPort.indexOf(']');
    if (close < 0) return false;
    host = hostPort.slice(0, close + 1);
    const after = hostPort.slice(close + 1);
    if (after !== '' && !after.startsWith(':')) return false;
    port = after === '' ? null : after.slice(1);
    if (!isIpv6(host)) return false;
  } else {
    const colon = hostPort.lastIndexOf(':');
    host = colon < 0 ? hostPort : hostPort.slice(0, colon);
    port = colon < 0 ? null : hostPort.slice(colon + 1);
    if (!isDnsOrIpv4(host)) return false;
  }
  // An empty port ("example.com:/") is allowed; otherwise digits up to 65535.
  if (port !== null && port !== '' && !(/^\d+$/.test(port) && Number(port) <= 65535)) return false;
  return true;
}

function isDnsOrIpv4(host) {
  if (host.length === 0) return false;
  const labels = (host.endsWith('.') ? host.slice(0, -1) : host).split('.');
  return labels.every(label => label.length > 0 && HOST_LABEL.test(label));
}

function isIpv6(bracketed) {
  if (!/^\[[0-9a-fA-F:.]+\]$/.test(bracketed) || !bracketed.includes(':')) return false;
  try {
    return new URL(`http://${bracketed}/`).hostname.startsWith('[');
  } catch {
    return false;
  }
}
