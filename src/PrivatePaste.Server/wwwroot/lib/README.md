# Vendored libraries

Served from `/lib`, never from a CDN: the CSP is `script-src 'self' 'wasm-unsafe-eval'`.

## hash-wasm 4.12.0 (Argon2id)

- File: `hash-wasm/argon2.umd.min.js` (UMD build, sets `globalThis.hashwasm`), licence in `hash-wasm/LICENSE` (MIT).
- Source: `npm pack hash-wasm@4.12.0` (published 2024-11-19), file `package/dist/argon2.umd.min.js`.
- SHA-256 of `argon2.umd.min.js`: `dcec617a2e1b700fa132d1583a186cb70611113395e869f2dd6cc82b415d3094`
- SHA-256 of `LICENSE`: `c14dea172f72f2714284a0ac2ab1b00b5352a01409d58255a46227ffc541debd`

To update, pack the new version in a scratch directory, copy only these two files, and update this record.

## qrcode-generator 1.5.2 (QR codes)

- File: `qrcode-generator/qrcode.js` (classic script, sets the global `qrcode`), licence in `qrcode-generator/LICENSE` (MIT,
  Copyright (c) 2009 Kazuhiko Arase).
- Source: `npm pack qrcode-generator@1.5.2` (published 2025-06-26), file `package/qrcode.js`. The npm package has no
  licence file, so `LICENSE` is the one from the project's repository (https://github.com/kazuhikoarase/qrcode-generator).
- SHA-256 of `qrcode.js`: `18ae399f81182bc9de916e9c77b195df20cc58d6f2d55a62b085a299f1bf1780`
- SHA-256 of `LICENSE`: `3a850fa5f08101db6f40676c2786e10bd2cd5fff7b12ffdf1e0c434d4e49d90c`
- Only the matrix is used (`isDark`, `getModuleCount`); `js/settings-encryption.js` draws it to a canvas, so no inline
  style or markup is needed. `Settings.cshtml` requests it as `qrcode.js?v=1.5.2`; change that with the version.

## Common-passphrase list

`../js/common-passphrases.txt` is the list `passphraseProblem()` rejects against, and the same file is shipped
on every platform (each project's test compares its SHA-256 with this one).

- Source: entries of 12 or more characters (the passphrase minimum; shorter ones are rejected by length anyway) from
  SecLists `Passwords/Common-Credentials` (https://github.com/danielmiessler/SecLists), MIT licence,
  Copyright (c) 2018 Daniel Miessler: `100k-most-used-passwords-NCSC.txt`, then
  `xato-net-10-million-passwords-1000000.txt` in rank order until the size cap (about 285 KB, 19,992 entries).
  `Pwdb_top-1000000.txt` was also tried and contributed nothing within the cap. SecLists has no phrase-style
  list with a clear licence, so none was added.
- Processed: NFC, lowercased, deduplicated, length >= 12 UTF-16 units, `#` header lines added.
- SHA-256: `8ce67ae3206188e600ba31179311f8a937d359cdcc963dae997d42e2ca9cb75f`
- The code requests it as `common-passphrases.txt?v=8ce67ae3` (first 8 hex of the SHA-256); update `LIST_VERSION`
  in `js/e2e.js` with the file, likewise `HASHWASM_VERSION` for the vendored script.
