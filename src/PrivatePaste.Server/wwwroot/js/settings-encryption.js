// The Settings page's encryption panels: change the passphrase, add a device by QR code, reset encryption (the first
// and last of those for the owner). Everything is sealed and opened here; the server only stores wraps and one-time
// blobs. The vault key lives in sessionStorage (see vault-ui.js).
// Never log passphrases, keys or the QR text.

const shell = document.getElementById('add-device');
const e2e = await import(shell.dataset.e2e);
const { readKey, storeKey, MISMATCH } = await import(shell.dataset.vaultUi);

const NOT_OWNER = 'Only the owner can change the passphrase.';
const CHANGE_FAILED = "Couldn't change the passphrase. Try again.";
const RESET_FAILED = "Couldn't reset encryption. Try again.";
const NO_CODE = "Couldn't make a code. Check your connection.";
const CHANGED = 'Passphrase changed.';
const CURRENT_WRONG = 'The current passphrase is wrong.';
const UNSUPPORTED = "This server's encryption settings aren't supported. Update PrivatePaste.";
const NOT_SET_UP_OWNER = 'Encryption is not set up yet. Set it up on the History page.';
const NOT_SET_UP = 'Ask the owner to set up encryption.';
const LOCKED = 'Unlock your history to add a device.';

const HANDOVER_SECONDS = 10 * 60;

const $ = id => document.getElementById(id);

/** fetch with the protocol header, and the antiforgery token on anything but a GET. */
function api(path, init = {}) {
  const headers = new Headers(init.headers);
  headers.set('X-PrivatePaste-Protocol', '2');
  if (init.method && init.method !== 'GET')
    headers.set('RequestVerificationToken', document.querySelector('meta[name="csrf-token"]')?.content ?? '');
  return fetch(path, { ...init, headers });
}

const postJson = (path, method, body) => api(path, {
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

function setText(element, text) {
  element.textContent = text ?? '';
  element.hidden = !text;
}

function setBusy(form, busy) {
  for (const control of form.elements) control.disabled = busy;
  form.setAttribute('aria-busy', String(busy));
}

async function fetchWrap() {
  const response = await api('/api/vault');
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`Vault fetch failed (${response.status})`);
  return response.json();
}

/** This tab's vault when its key opens the server's current vault; null otherwise. */
async function tabVault(wrap) {
  const raw = readKey();
  if (!raw) return null;
  try {
    const vault = await e2e.importVaultKey(raw);
    return vault.keyId === wrap?.keyId ? vault : null;
  } catch {
    return null;
  } finally {
    raw.fill(0);
  }
}

const addOpen = $('add-device-open');
const note = $('encryption-note');
const resetOpen = $('reset-open');
const changeForm = $('cp-form');
const isOwner = changeForm !== null;

// What each panel can do depends on the server's vault and whether this tab holds its key.
async function refresh() {
  let wrap = null;
  let unlocked = false;
  try {
    wrap = await fetchWrap();
    const vault = await tabVault(wrap);
    unlocked = vault !== null;
    vault?.raw.fill(0);
  } catch (e) {
    console.error(e);
  }
  const noVault = wrap === null;
  addOpen.disabled = !unlocked;
  if (resetOpen) resetOpen.disabled = noVault;
  if (changeForm) setBusy(changeForm, noVault);
  setText(note, noVault ? (isOwner ? NOT_SET_UP_OWNER : NOT_SET_UP) : unlocked ? null : LOCKED);
}

// ---- change passphrase ----

changeForm?.addEventListener('submit', async event => {
  event.preventDefault();
  const current = $('cp-current');
  const next = $('cp-new');
  const repeat = $('cp-repeat');
  setText($('cp-error'), null);
  setText($('cp-status'), null);
  setBusy(changeForm, true);
  let vault = null;
  try {
    const problem = await e2e.passphraseProblem(next.value);
    if (problem || next.value !== repeat.value) {
      setText($('cp-error'), problem ?? MISMATCH);
      return;
    }
    // Fetched now, so a reset since the page loaded is met with the new wrap.
    const wrap = await fetchWrap();
    if (!wrap) throw new Error('No vault.');
    vault = await e2e.unwrapWithPassphrase(wrap, current.value);
    const response = await postJson('/api/vault', 'PUT', await e2e.wrapWithPassphrase(vault, next.value));
    if (response.status === 403) {
      setText($('cp-error'), NOT_OWNER);
      return;
    }
    if (!response.ok) throw new Error(`Change failed (${response.status})`);
    current.value = next.value = repeat.value = '';
    setText($('cp-status'), CHANGED);
  } catch (e) {
    if (e instanceof e2e.WrongPassphraseError) {
      setText($('cp-error'), CURRENT_WRONG);
    } else if (e instanceof e2e.UnsupportedWrapError) {
      setText($('cp-error'), UNSUPPORTED);
    } else {
      console.error(e);
      setText($('cp-error'), CHANGE_FAILED);
    }
  } finally {
    vault?.raw.fill(0);
    setBusy(changeForm, false);
  }
});

// ---- add a device ----

const addDialog = $('add-device-dialog');
const codeBox = $('add-device-code');
const qrCanvas = $('qr-code');
const timeLeft = $('add-device-time');
const newCode = $('add-device-new');
let countdown = null;
// Bumped whenever a code is dropped, so a handover still in flight when the dialog closes is not shown.
let codeRun = 0;

/** Hides the code and forgets its text: the attribute, the canvas and the timer. */
function dropCode() {
  codeRun++;
  clearInterval(countdown);
  countdown = null;
  qrCanvas.removeAttribute('data-qr');
  qrCanvas.width = qrCanvas.height = 0;
  codeBox.hidden = true;
  timeLeft.textContent = '';
}

function drawQr(text) {
  const qr = globalThis.qrcode(0, 'M');
  qr.addData(text);
  qr.make();
  const count = qr.getModuleCount();
  const cell = 6;
  const quiet = 4; // modules of white around the code, which scanners need
  const size = (count + quiet * 2) * cell;
  qrCanvas.width = qrCanvas.height = size;
  const g = qrCanvas.getContext('2d');
  // Black on white in both themes: scanners expect dark modules on a light ground.
  g.fillStyle = '#ffffff';
  g.fillRect(0, 0, size, size);
  g.fillStyle = '#000000';
  for (let row = 0; row < count; row++)
    for (let col = 0; col < count; col++)
      if (qr.isDark(row, col)) g.fillRect((col + quiet) * cell, (row + quiet) * cell, cell, cell);
}

const clock = seconds => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;

async function showCode() {
  dropCode();
  const run = codeRun;
  setText($('add-device-error'), null);
  newCode.hidden = true;
  let text = null;
  try {
    const vault = await tabVault(await fetchWrap());
    if (!vault) throw new Error('The history is locked.');
    const { otk, blob } = await e2e.createHandover(vault);
    try {
      const response = await postJson('/api/vault/handovers', 'POST', { blob: btoa(String.fromCharCode(...blob)) });
      if (!response.ok) throw new Error(`Handover failed (${response.status})`);
      text = e2e.handoverQrText((await response.json()).id, otk, vault.keyId);
    } finally {
      otk.fill(0);
      vault.raw.fill(0);
    }
  } catch (e) {
    console.error(e);
  }
  if (run !== codeRun) return; // closed or replaced meanwhile
  if (text === null) {
    setText($('add-device-error'), NO_CODE);
    newCode.hidden = false;
    return;
  }
  drawQr(text);
  qrCanvas.setAttribute('data-qr', text);
  codeBox.hidden = false;
  const endsAt = Date.now() + HANDOVER_SECONDS * 1000;
  const tick = () => {
    const left = Math.ceil((endsAt - Date.now()) / 1000);
    if (left <= 0) {
      dropCode();
      newCode.hidden = false;
      return;
    }
    timeLeft.textContent = `${clock(left)} left`;
  };
  tick();
  countdown = setInterval(tick, 1000);
}

addOpen.addEventListener('click', () => {
  addDialog.showModal();
  showCode();
});
newCode.addEventListener('click', showCode);
$('add-device-close').addEventListener('click', () => addDialog.close());
addDialog.addEventListener('close', dropCode);

// Lock (in the top bar) forgets the key: the code goes with it.
document.addEventListener('pp:locked', () => {
  if (addDialog.open) addDialog.close();
  refresh();
});

// ---- reset encryption ----

const resetDialog = $('reset-dialog');
if (resetDialog) {
  const form = $('reset-form');
  const clearForm = () => {
    form.reset();
    setText($('reset-error'), null);
  };
  resetOpen.addEventListener('click', () => resetDialog.showModal());
  $('reset-cancel').addEventListener('click', () => resetDialog.close());
  resetDialog.addEventListener('close', clearForm);

  form.addEventListener('submit', async event => {
    event.preventDefault();
    const error = $('reset-error');
    setText(error, null);
    setBusy(form, true);
    try {
      const passphrase = $('reset-new').value;
      const problem = await e2e.passphraseProblem(passphrase);
      if (problem || passphrase !== $('reset-repeat').value) {
        setText(error, problem ?? MISMATCH);
        return;
      }
      const raw = crypto.getRandomValues(new Uint8Array(32));
      let vault;
      try { vault = await e2e.importVaultKey(raw); } finally { raw.fill(0); }
      const response = await postJson('/api/vault/reset', 'POST', await e2e.wrapWithPassphrase(vault, passphrase));
      if (!response.ok) throw new Error(`Reset failed (${response.status})`);
      // This tab now holds the new key; every other device asks for the new passphrase.
      storeKey(vault.raw);
      vault.raw.fill(0);
      resetDialog.close();
      const lock = $('lock');
      if (lock) lock.hidden = false;
      setText($('cp-status'), null);
      setText($('cp-error'), null);
      await refresh();
    } catch (e) {
      console.error(e);
      setText(error, RESET_FAILED);
    } finally {
      setBusy(form, false);
    }
  });
}

await refresh();
