# Vendored libraries

Served from `/lib`, never from a CDN: the CSP is `script-src 'self' 'wasm-unsafe-eval'`.

## hash-wasm 4.12.0 (Argon2id)

- File: `hash-wasm/argon2.umd.min.js` (UMD build, sets `globalThis.hashwasm`), licence in `hash-wasm/LICENSE` (MIT).
- Source: `npm pack hash-wasm@4.12.0` (published 2024-11-19), file `package/dist/argon2.umd.min.js`.
- SHA-256 of `argon2.umd.min.js`: `dcec617a2e1b700fa132d1583a186cb70611113395e869f2dd6cc82b415d3094`
- SHA-256 of `LICENSE`: `c14dea172f72f2714284a0ac2ab1b00b5352a01409d58255a46227ffc541debd`

To update, pack the new version in a scratch directory, copy only these two files, and update this record.

## Common-passphrase list

`../js/common-passphrases.txt` is the list `passphraseProblem()` rejects against, and the same file is shipped
on every platform (each project's test compares its SHA-256 with this one).

- Source: the top 10,000 of SecLists `Passwords/Common-Credentials/Pwdb_top-10000.txt`
  (https://github.com/danielmiessler/SecLists), MIT licence, Copyright (c) 2018 Daniel Miessler.
- Processed: lowercased, deduplicated, `#` header lines added.
- SHA-256: `abb2d64604a78db21d04fb0376bbeb1c2bd62b1e0a45ab9fa18f11d3a1f69fb3`
