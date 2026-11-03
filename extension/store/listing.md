# Edge Add-ons listing

Everything the Partner Center form asks for, in the order of its pages, ready to paste. Limits are from
[Publish a Microsoft Edge extension](https://learn.microsoft.com/en-us/microsoft-edge/extensions/publish/publish-extension)
(checked 2026-11-03). The step-by-step is in [README.md](README.md).

Store copy rules: the listing must not name other browsers (developer policy 1.1.2), and must say that the extension
needs other software, here a PrivatePaste server (policy 1.2.3).

## Developer account (registration, once)

| Field | Value |
|---|---|
| Account type | Individual |
| Publisher display name | `Ivan Ficko` (shown on the listing) |
| Contact email | `privacy@ficdev.xyz` (the privacy contact; the same address the privacy policy names) |

## Package (from the zip, read-only in the form)

| Field | Value | Where it comes from |
|---|---|---|
| Extension name | `PrivatePaste` | manifest `name` |
| Short description | `Self-hosted, end-to-end encrypted clipboard sync with your other devices.` (73 characters) | manifest `description`; changing it needs a new zip |
| Version | `0.6.2` | `--version` of `build.mjs` |

## Availability

- **Visibility:** Public.
- **Markets:** all markets (the default).

## Properties

| Field | Value |
|---|---|
| Category | Productivity |
| Website | `https://privatepaste.ficdev.xyz` |
| Support contact detail | `https://github.com/DynamicsNinja/privatepaste/issues` |
| Mature content | Leave unchecked |

## Privacy

### Single purpose description

```
PrivatePaste keeps the clipboard of this computer in sync with the user's other devices through the user's own self-hosted PrivatePaste server: what the user copies is end-to-end encrypted and sent to that server, clips from the user's other devices are put on the clipboard, and the toolbar popup shows the shared, searchable clip history.
```

### Permission justification

One per permission the form lists:

| Permission | Justification |
|---|---|
| `clipboardRead` | PrivatePaste reads what you copy so it can send it, encrypted, to your own server and your other devices. |
| `clipboardWrite` | Puts the text and links you copied on your other devices on this computer's clipboard, and lets Copy in the popup put a clip from the history back on it. |
| `offscreen` | The clipboard can only be read from a document, so a hidden offscreen document (reason CLIPBOARD) reads it once a second and keeps the sync connection to your server open while Edge runs. |
| `alarms` | A one-minute alarm re-creates the offscreen document if Edge has closed it, so syncing carries on without the user opening the popup. |
| Host permissions (`optional_host_permissions`: `http://*/*`, `https://*/*`) | PrivatePaste is self-hosted, so the server address is only known when the user signs in; the extension then asks for that one origin and talks to no other site (plain http is accepted for localhost only). |

### Are you using remote code?

**No, I am not using remote code.** All code is in the package. The Argon2id library
(`lib/hash-wasm/argon2.umd.min.js`) is the unmodified, minified build of the open-source npm package
`hash-wasm@4.12.0` (MIT, licence file next to it); its WebAssembly is embedded in that file, which is why the CSP has
`'wasm-unsafe-eval'`. Nothing is fetched and run from the network.

### Data usage

What user data do you plan to collect:

- **Authentication information:** yes. The name and password are sent once to the user's own server to sign in; the
  server's device token is kept in the browser profile.
- **Website content:** yes. Clipboard text and images (what the user copies), end-to-end encrypted, sent to the user's
  own server only.
- Everything else (personally identifiable information, health, financial, personal communications, location, web
  history, user activity): no. If the form asks about personal communications, clipboard contents can contain anything
  the user copies; the owner may tick it to be conservative.

Tick all three certifications: no selling or transfer to third parties outside the approved use cases, no use unrelated
to the single purpose, no use for creditworthiness or lending. They are all true: the data goes only to the server the
user signs in to, which the user runs, and never to the developer.

### Privacy policy URL

```
https://privatepaste.ficdev.xyz/privacy.html
```

If the form asks for a privacy contact, it is `privacy@ficdev.xyz`.

## Store listing (English)

### Description (2,283 characters; the limit is 250 to 10,000)

```
Copy on your laptop, paste on this computer. PrivatePaste keeps the clipboard in sync across your devices and keeps a searchable history of everything you copy, through a PrivatePaste server that you run yourself.

Made for computers where you can't install the PrivatePaste desktop app, such as a work PC: the extension does the same job from the browser.

What it does
• Copies you make in any app on this computer are sent to your other devices while Edge is running.
• Text and links copied on your phone or another computer land on this computer's clipboard a moment later.
• Images and files from your other devices arrive in the history: Copy puts an image on the clipboard, Download saves a file.
• The toolbar popup shows the shared history, newest first, with the device each clip came from. Search it, filter by Text, Links, Images or Files, and keep the clips you reuse on the Pinned tab.
• Pause syncing whenever you like: nothing is read, sent or received while paused.

Private by design
• Clips are end-to-end encrypted on this computer (AES-256-GCM) before they leave it, with a key unlocked by your passphrase. The server stores only ciphertext.
• The extension talks only to the server you sign in to. No analytics, no tracking, no ads, and nothing is sent to the developer.
• Sign out and the extension deletes its sign-in, keys, cached history and unsent copies from your Edge profile.

What you need
• A PrivatePaste server. It is free and open source (MIT) and runs as one small Docker container; setup takes a few minutes: https://privatepaste.ficdev.xyz
• Your server's address (https://), your name and password, and the encryption passphrase you chose on the server.

Good to know
• It syncs only while Edge is running. Copies made while Edge is closed are not sent later.
• Copies from password managers sync too: the extension can't tell them apart from other copies. They are encrypted like every clip; use Pause syncing before copying a secret.
• Files copied in File Explorer are not sent; text and images are. Files from your other devices can be downloaded from the history.
• Apps for Windows, macOS, Linux and Android, and a web app, sync with the same server.

Source code, documentation and support: https://github.com/DynamicsNinja/privatepaste
```

### Images

All in [images/](images/), made by `node extension/store/make-images.mjs`.

| Field | File | Size |
|---|---|---|
| Extension logo | `store-logo-300.png` | 300 x 300 (recommended 300 x 300, minimum 128 x 128, 1:1) |
| Small promotional tile | `promo-small-440x280.png` | 440 x 280 |
| Large promotional tile | `promo-large-1400x560.png` | 1400 x 560 |
| Screenshot 1 | `screenshot-1-history.png` | 1280 x 800 |
| Screenshot 2 | `screenshot-2-pinned.png` | 1280 x 800 |
| Screenshot 3 | `screenshot-3-settings.png` | 1280 x 800 |
| Screenshot 4 | `screenshot-4-signin.png` | 1280 x 800 |

Screenshots may be 640 x 480 or 1280 x 800, at most 6. The YouTube video URL stays empty.

### Search terms (7 terms, 17 words, the longest 26 characters; the limit is 7 terms, 30 characters each, 21 words)

```
clipboard sync
clipboard history
self-hosted
end-to-end encrypted
copy paste between devices
clipboard manager
sync clipboard to phone
```

## Notes for certification

Paste as is. It names no real server or password; the reviewer chooses their own.

```
PrivatePaste is self-hosted: the extension syncs the clipboard through a PrivatePaste server that the user runs, so there is no shared test account. The server is free and open source (https://github.com/DynamicsNinja/privatepaste). To test, run one locally with Docker (one container, about a minute):

1. Start a server (any values you like for the two variables; PP_DB_KEY needs at least 16 characters):

   docker run -d --name privatepaste-review -p 8080:8080 -e PP_DB_KEY=review-only-key-0123456789 -e PP_PASSWORD=review-password ghcr.io/dynamicsninja/privatepaste:0.6.2

   Wait until http://localhost:8080/health answers "Healthy".

2. Set up encryption in the web app first: open http://localhost:8080, sign in with the name "owner" and the PP_PASSWORD from step 1 (review-password), and choose an encryption passphrase when asked ("Set up encryption"; at least 12 characters, for example "review passphrase 2026"). The History page then opens, empty.

3. Sign in the extension: click the PrivatePaste toolbar icon. While signed out it opens its sign-in form in a tab. Enter:
   Server URL: http://localhost:8080   (plain http is accepted for localhost only; real servers use https)
   Name: owner
   Password: review-password
   Device name: Edge (the default)
   Click Sign in and choose Allow when Edge asks to let the extension access localhost:8080 (the optional host permission, requested for that one server). Then enter the passphrase from step 2 and click Unlock. The toolbar popup now shows "Syncing".

4. Sending: copy some text in any app (for example Notepad). Within a second or two it appears at the top of the popup's history, and after a reload of http://localhost:8080 in the web app's History, marked as coming from the "Browser extension" device "Edge". Copy a screenshot (Win+Shift+S) and it appears as an image row.

5. Receiving: the web app only shows the history, so a second device is needed to send to this one. Create a second Edge profile, install the extension there too and sign in as in step 3 with Device name "Edge 2". Text copied in one profile's window lands on the clipboard and appears in the other profile's popup. Copy on a row in the popup puts that clip back on the clipboard.

6. Other features: search box and the All/Text/Links/Images/Files chips filter the history; the pin button in the web app pins a clip and it shows on the popup's Pinned tab; Settings (gear icon) has Pause syncing (status shows "Paused"; nothing is read or sent) and Sign out (deletes the token, keys and cached history; the popup returns to sign-in).

Notes:
- Permissions: clipboardRead/clipboardWrite to sync the clipboard; offscreen because the clipboard is read from an offscreen document (reason CLIPBOARD) once a second while Edge runs; alarms to re-create that document if it was closed; optional host permissions because the server address is only known at sign-in, and only that origin is requested.
- lib/hash-wasm/argon2.umd.min.js is the unmodified hash-wasm 4.12.0 npm build (MIT) for Argon2id passphrase hashing; it embeds WebAssembly, hence 'wasm-unsafe-eval' in the CSP. No remote code.
- The extension sends data only to the server the user signs in to; clips are end-to-end encrypted on the device before upload. Privacy policy: https://privatepaste.ficdev.xyz/privacy.html (privacy contact: privacy@ficdev.xyz)
- To clean up: docker rm -f privatepaste-review
```
