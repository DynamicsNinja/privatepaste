// The vault panels on the history page: unlock, the owner's first setup, "ask the owner", and Lock.
// The vault key lives in this tab's sessionStorage under STORAGE_KEY (base64) and nowhere else; Lock and Sign out
// (site.js) remove it. Never log passphrases or keys.

const STORAGE_KEY = 'pp.vault';

const WRONG_PASSPHRASE = "That passphrase doesn't unlock this server's history.";
const UNSUPPORTED = "This server's encryption settings aren't supported. Update PrivatePaste.";
const RESET = 'Encryption was reset. Enter the new passphrase.';
const MISMATCH = "The passphrases don't match.";
const SET_UP_ELSEWHERE = 'Encryption was already set up on another device. Enter its passphrase.';
const FAILED = 'Something went wrong. Try again.';

const $ = id => document.getElementById(id);

function readKey() {
  try {
    const text = sessionStorage.getItem(STORAGE_KEY);
    return text ? Uint8Array.from(atob(text), c => c.charCodeAt(0)) : null;
  } catch {
    return null;
  }
}

function storeKey(raw) {
  try {
    sessionStorage.setItem(STORAGE_KEY, btoa(String.fromCharCode(...raw)));
  } catch {
    // Storage blocked: the history stays unlocked until this page is left.
  }
}

function forgetKey() {
  try { sessionStorage.removeItem(STORAGE_KEY); } catch { /* nothing stored */ }
}

function setError(element, text) {
  element.textContent = text ?? '';
  element.hidden = !text;
}

function setBusy(form, busy) {
  for (const control of form.elements) control.disabled = busy;
  form.setAttribute('aria-busy', String(busy));
}

/**
 * Shows the panel that fits the server's vault and this tab's key, and calls `onUnlocked(vault)` once the history can
 * be read. `onLocked()` runs when Lock is pressed (site.js removes the key and sends "pp:locked").
 */
export async function startVault(e2e, api, { onUnlocked, onLocked }) {
  const panels = [$('unlock'), $('setup'), $('no-vault')];
  const show = panel => { for (const p of panels) p.hidden = p !== panel; };
  let isOwner = false;

  async function fetchWrap() {
    const response = await api('/api/vault');
    if (response.status === 404) return null;
    if (!response.ok) throw new Error(`Vault fetch failed (${response.status})`);
    return response.json();
  }

  function unlocked(vault) {
    show(null);
    $('lock').hidden = false;
    onUnlocked(vault);
  }

  // Every outcome but unlocked() leaves the tab without a usable key, so the key and Lock go.
  function locked() {
    forgetKey();
    $('lock').hidden = true;
  }

  async function resolve(notice) {
    const wrap = await fetchWrap();
    if (!wrap) {
      locked();
      show(isOwner ? $('setup') : $('no-vault'));
      if (isOwner) $('setup-passphrase').focus();
      return;
    }
    const raw = readKey();
    if (raw) {
      let vault = null;
      try { vault = await e2e.importVaultKey(raw); } catch { /* not a key: unlock again */ } finally { raw.fill(0); }
      if (vault?.keyId === wrap.keyId) return unlocked(vault);
      // The server's vault has another key now: the owner reset encryption since this tab unlocked.
      if (vault) notice = RESET;
    }
    locked();
    setError($('unlock-error'), notice);
    show($('unlock'));
    $('unlock-passphrase').focus();
  }

  $('unlock-form').addEventListener('submit', async event => {
    event.preventDefault();
    const form = event.currentTarget;
    const input = $('unlock-passphrase');
    const error = $('unlock-error');
    setError(error, null);
    setBusy(form, true);
    try {
      // Fetched again so a reset since the page loaded unlocks with the new wrap.
      const wrap = await fetchWrap();
      if (!wrap) {
        await resolve();
        return;
      }
      const vault = await e2e.unwrapWithPassphrase(wrap, input.value);
      input.value = '';
      storeKey(vault.raw);
      unlocked(vault);
    } catch (e) {
      if (e instanceof e2e.WrongPassphraseError) setError(error, WRONG_PASSPHRASE);
      else if (e instanceof e2e.UnsupportedWrapError) setError(error, UNSUPPORTED);
      else {
        console.error(e);
        setError(error, FAILED);
      }
    } finally {
      setBusy(form, false);
    }
    if (!error.hidden) input.select();
  });

  $('setup-form').addEventListener('submit', async event => {
    event.preventDefault();
    const form = event.currentTarget;
    const first = $('setup-passphrase');
    const repeat = $('setup-repeat');
    const error = $('setup-error');
    setError(error, null);
    setBusy(form, true);
    try {
      const passphrase = first.value;
      const problem = await e2e.passphraseProblem(passphrase);
      if (problem || passphrase !== repeat.value) {
        setError(error, problem ?? MISMATCH);
        return;
      }
      const raw = crypto.getRandomValues(new Uint8Array(32));
      let vault;
      try { vault = await e2e.importVaultKey(raw); } finally { raw.fill(0); }
      const wrap = await e2e.wrapWithPassphrase(vault, passphrase);
      const response = await api('/api/vault', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(wrap),
      });
      first.value = repeat.value = '';
      // Set up from another device in the meantime: unlock with that passphrase instead.
      if (response.status === 409) {
        await resolve(SET_UP_ELSEWHERE);
        return;
      }
      if (!response.ok) throw new Error(`Setup failed (${response.status})`);
      storeKey(vault.raw);
      unlocked(vault);
    } catch (e) {
      console.error(e);
      setError(error, FAILED);
    } finally {
      setBusy(form, false);
    }
  });

  document.addEventListener('pp:locked', () => {
    onLocked();
    resolve().catch(e => {
      console.error(e);
      setError($('unlock-error'), FAILED);
      show($('unlock'));
    });
  });

  const me = await api('/api/me');
  if (!me.ok) throw new Error(`Who-am-I failed (${me.status})`);
  isOwner = (await me.json()).isOwner === true;
  await resolve();
}
