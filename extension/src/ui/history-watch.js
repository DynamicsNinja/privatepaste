// The popup's refresh tick: asks the engine for its history version (a string, no decrypting) and reports whether it
// changed since the last look, so the rows are only asked for (and decrypted) when something happened.

export function historyWatch(ask) {
  let seen = null;
  return {
    /** True the first time and whenever the engine's history changed since the last call. */
    async changed() {
      const version = await ask({ type: 'engine.historyVersion' });
      if (version === seen) return false;
      seen = version;
      return true;
    },
    /** After the rows couldn't be fetched: the next look counts as a change again. */
    forget() {
      seen = null;
    },
  };
}
