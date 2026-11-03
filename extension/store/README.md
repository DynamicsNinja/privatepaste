# Publishing to Edge Add-ons

For the repository owner: everything needed to list the extension on
[Microsoft Edge Add-ons](https://microsoftedge.microsoft.com/addons). The texts to paste are in
[listing.md](listing.md), the images in [images/](images/). Requirements were checked against Microsoft Learn on
2026-11-03 (sources at the end).

## 1. Create the developer account (once, yourself)

1. Go to [Partner Center](https://partner.microsoft.com/dashboard/microsoftedge/public/login) and sign in with a
   personal Microsoft account (Outlook.com, Live.com, Hotmail.com) or a GitHub account. Work or school accounts are
   not accepted for registering.
2. The **Microsoft Edge Developer Account Registration** form opens (otherwise **Account settings > Programs >
   Microsoft Edge > Get started**).
3. Pick the country, then **Individual** as the account type: its verification is short (the publisher name must be
   free). Company accounts take days to weeks and need a company approver. The account type and country can't be
   changed later.
4. Publisher display name (at most 50 characters; shown on the listing), contact details, accept the App Developer
   Agreement, **Finish**.

It is free: Edge Add-ons has no registration fee.

## 2. The zip

Upload `privatepaste-extension-0.6.2.zip` from the
[extension-v0.6.2 release](https://github.com/DynamicsNinja/privatepaste/releases/tag/extension-v0.6.2) as it is. The
CI zip is what the store wants: `manifest.json` at the root of the zip, `version` `0.6.2`, only the files the extension
runs (no fake clipboard, no tests), and no remote code. To build the same thing yourself:

```
node extension/build.mjs --version 0.6.2
```

then zip the contents of `extension/dist/` (not the folder itself).

The manifest was checked for what the review rejects and nothing had to change:

- `version` is plain numbers (`build.mjs` moves a prerelease suffix to `version_name`).
- `'wasm-unsafe-eval'` in the CSP is allowed in Manifest V3; it is there for the Argon2 WebAssembly embedded in
  `lib/hash-wasm/argon2.umd.min.js`, the unmodified `hash-wasm` 4.12.0 build. The certification notes say so, because
  minified code gets a closer look (obfuscated code is not allowed; a minified open-source library is).
- Host access is only optional (`optional_host_permissions`), asked for at sign-in for the one server.
- The 73-character `description` becomes the listing's short description.

`extension/test/build.test.mjs` ("the release manifest stays within the store rules") keeps it that way.

## 3. Fill in Partner Center

In Partner Center: **Edge > Overview > Create new extension**, drop the zip on **Upload package**, **Continue**. Then
each page in the left column, with [listing.md](listing.md) open next to it:

| Page | Field | Paste or upload |
|---|---|---|
| Availability | Visibility, Markets | Public, all markets |
| Properties | Category | Productivity |
| | Website | `https://privatepaste.ficdev.xyz` |
| | Support contact detail | `https://github.com/DynamicsNinja/privatepaste/issues` |
| | Mature content | unchecked |
| Privacy | Single purpose description | listing.md, "Single purpose description" |
| | Permission justification | one row per permission from the listing.md table |
| | Are you using remote code? | No |
| | Data usage | the boxes listed in listing.md, and all three certifications |
| | Privacy policy URL | `https://privatepaste.ficdev.xyz/privacy.html` (live once the site is deployed from `main`) |
| Store listings > English > Edit details | Description | listing.md, "Description" |
| | Extension logo | `images/store-logo-300.png` |
| | Small promotional tile | `images/promo-small-440x280.png` |
| | Large promotional tile | `images/promo-large-1400x560.png` |
| | Screenshots | `images/screenshot-1-history.png` to `screenshot-4-signin.png`, in that order |
| | YouTube video URL | empty |
| | Search terms | the seven lines in listing.md, one per box |
| Publish (top right) | Notes for certification | listing.md, "Notes for certification" |

Name and short description come from the manifest and can't be edited in the form. **Save** on each page, then
**Publish**.

The privacy page must be reachable before you submit: merge this branch and let the homepage deploy, then open
https://privatepaste.ficdev.xyz/privacy.html once.

## 4. Review

Certification takes **up to seven business days**. The status in Partner Center goes from "In review" to "In the
Store"; you get an email either way. A rejection names the policy; fix it, upload again and publish.

The weak spot is testability: the developer policies (1.3) ask for a working server and test credentials. The
notes give a one-command local server instead (Docker, `ghcr.io/dynamicsninja/privatepaste:0.6.2`), because
PrivatePaste has no shared server. If a reviewer can't run Docker and rejects for that, the fallback is a throwaway
review server on its own domain with a review-only password, put in the notes and taken down after approval. Never
the real server.

After approval, the listing URL is on the extension's **Overview** page. Put it in `README.md`, `docs/extension.md`
and the homepage's Edge pill, replacing the "coming soon" lines.

## 5. Updates

1. Bump the version as usual and push the tag `extension-vX.Y.Z`; CI builds `privatepaste-extension-X.Y.Z.zip`. The
   version must be higher than the one in the store.
2. In Partner Center: the extension > **Packages** > replace the package with the new zip.
3. Change listing texts or images only if they changed.
4. **Publish**, with a line in the notes for certification on what changed.

Updates are certified again (again up to seven business days); users get them automatically. A prerelease tag
(`0.7.0-rc1`) uploads as version `0.7.0`, so don't send release candidates to the store, or the final can't reuse the
number.

Later, uploads can be automated with the
[Edge Add-ons REST API](https://learn.microsoft.com/en-us/microsoft-edge/extensions-chromium/publish/api/using-addons-api)
(v1.1: enable it on Partner Center's **Publish API** page for a client ID and API key, then upload the zip and publish
from CI). It only updates existing extensions and can't change listing texts. Not set up yet.

## Chrome Web Store

The same zip would load in Chrome (Manifest V3, Chrome 116+ for the offscreen API), but:

- **Fee:** a one-time registration fee (US$5, per Google's agreement and long-standing reports; the dashboard shows
  the current amount), covering every item the account publishes.
- **Not tested in Chrome.** The extension is only tested in Edge, and the docs say "Edge only". Run the hand-run
  Playwright checks in Chrome before listing it there.
- **Images:** a 128 x 128 store icon (the 300 x 300 logo scaled down), at least one 1280 x 800 screenshot (the same
  ones), the 440 x 280 small tile; the 1400 x 560 marquee is optional.
- **Privacy:** the same kind of Privacy tab (single purpose, a justification per permission, data use
  certifications) and a privacy policy URL, which is required. The privacy page and the listing text talk about Edge,
  so both need a Chrome wording first.

## Sources

- [Publish a Microsoft Edge extension](https://learn.microsoft.com/en-us/microsoft-edge/extensions/publish/publish-extension):
  form fields, image sizes, description and search-term limits, privacy page, notes for certification, review time.
- [Developer policies for the Microsoft Edge Add-ons website](https://learn.microsoft.com/en-us/legal/microsoft-edge/extensions/developer-policies):
  single purpose, no other browsers named, obfuscation, testability and credentials, privacy policy, permissions.
- [Register as a Microsoft Edge extension developer](https://learn.microsoft.com/en-us/microsoft-edge/extensions/publish/create-dev-account):
  no fee, account types and verification.
- [Update a Microsoft Edge extension](https://learn.microsoft.com/en-us/microsoft-edge/extensions/update/update-extension)
  and [Use the REST API to update an extension](https://learn.microsoft.com/en-us/microsoft-edge/extensions-chromium/publish/api/using-addons-api).
- Chrome Web Store: [Register your developer account](https://developer.chrome.com/docs/webstore/register),
  [Supplying images](https://developer.chrome.com/docs/webstore/images),
  [Developer agreement](https://developer.chrome.com/docs/webstore/terms/).

## Regenerating the images

```
node extension/store/make-images.mjs [--keep-out]
```

It needs the .NET SDK, Edge and Playwright from `scripts/readme` (`npm ci` there once). It starts a local throwaway
server (`PP_PASSWORD=dev`), seeds a made-up history from three made-up devices, drives a fake-clipboard build of the
popup in Edge and frames the shots with `frame.html`. It never touches the live server or the real clipboard.
`--keep-out` keeps the raw popup shots in `out/` (git-ignored). Look at every image before uploading. Rerun it when the
popup's look changes.
