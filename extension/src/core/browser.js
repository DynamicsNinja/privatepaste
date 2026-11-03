// The browser the extension runs in, for the default device name ("Edge", "Chrome", ...). The same build is published
// to the Edge and Chrome stores, so the name can't be fixed at build time.

const BRANDS = [
  ['Microsoft Edge', 'Edge'],
  ['Google Chrome', 'Chrome'],
  ['Opera', 'Opera'],
  ['Brave', 'Brave'],
];

// Without userAgentData (older or unusual builds) the user agent string is checked; Edge and Opera also contain
// "Chrome/", so they go first.
const AGENTS = [
  [/\bEdg\//, 'Edge'],
  [/\bOPR\//, 'Opera'],
  [/\bChrome\//, 'Chrome'],
];

/** A short name for this browser, or "Browser" when it can't tell. */
export function browserName(nav = globalThis.navigator) {
  const brands = nav?.userAgentData?.brands?.map(b => b.brand) ?? [];
  for (const [brand, name] of BRANDS) if (brands.includes(brand)) return name;
  if (brands.length === 0) {
    const agent = String(nav?.userAgent ?? '');
    for (const [pattern, name] of AGENTS) if (pattern.test(agent)) return name;
  }
  return 'Browser';
}
