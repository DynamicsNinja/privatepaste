# Chrome Web Store listing

The Chrome Web Store developer dashboard can't be filled in by browser automation (Chrome blocks scripting of store
pages), so this is everything to paste by hand, in the order of the dashboard's tabs. The same zip as for Edge works:
build it with `node extension/build.mjs --version <version>` and zip the folder's contents, or use the release asset
`privatepaste-extension-<version>.zip`. Images are the same as for Edge, in [images/](images/).

The default device name follows the browser (`src/core/browser.js`), so Chrome users see "Chrome".

## Package

Upload the zip (Items, New item).

## Store listing tab

| Field | Value |
|---|---|
| Description | the block below |
| Category | Workflow & Planning (or Tools) |
| Language | English |
| Store icon | `images/store-logo-300.png` (Chrome uses 128 x 128; the 300 x 300 file is scaled, or export a 128 one) |
| Screenshots | `images/screenshot-1-history.png` ... `screenshot-4-signin.png` (1280 x 800) |
| Small promo tile | `images/promo-small-440x280.png` |
| Marquee promo tile | `images/promo-large-1400x560.png` |
| Official URL / Homepage URL | `https://privatepaste.ficdev.xyz` |
| Support URL | `https://github.com/DynamicsNinja/privatepaste/issues` |

### Description

```
Copy on your laptop, paste on this computer. PrivatePaste keeps the clipboard in sync across your devices and keeps a searchable history of everything you copy, through a PrivatePaste server that you run yourself.

Made for computers where you can't install the PrivatePaste desktop app, such as a work PC: the extension does the same job from the browser.

What it does
â€˘ Copies you make in any app on this computer are sent to your other devices while Chrome is running.
â€˘ Text and links copied on your phone or another computer land on this computer's clipboard a moment later.
â€˘ Images and files from your other devices arrive in the history: Copy puts an image on the clipboard, Download saves a file.
â€˘ The toolbar popup shows the shared history, newest first, with the device each clip came from. Search it, filter by Text, Links, Images or Files, and keep the clips you reuse on the Pinned tab.
â€˘ Pause syncing whenever you like: nothing is read, sent or received while paused.

Private by design
â€˘ Clips are end-to-end encrypted on this computer (AES-256-GCM) before they leave it, with a key unlocked by your passphrase. The server stores only ciphertext.
â€˘ The extension talks only to the server you sign in to. No analytics, no tracking, no ads, and nothing is sent to the developer.
â€˘ Sign out and the extension deletes its sign-in, keys, cached history and unsent copies from your Chrome profile.

What you need
â€˘ A PrivatePaste server. It is free and open source (MIT) and runs as one small Docker container; setup takes a few minutes: https://privatepaste.ficdev.xyz
â€˘ Your server's address (https://), your name and password, and the encryption passphrase you chose on the server.

Good to know
â€˘ It syncs only while Chrome is running. Copies made while Chrome is closed are not sent later.
â€˘ Copies from password managers sync too: the extension can't tell them apart from other copies. They are encrypted like every clip; use Pause syncing before copying a secret.
â€˘ Files copied in File Explorer are not sent; text and images are. Files from your other devices can be downloaded from the history.
â€˘ Apps for Windows, macOS, Linux and Android, and a web app, sync with the same server.

Source code, documentation and support: https://github.com/DynamicsNinja/privatepaste
```

## Privacy practices tab

- **Single purpose:** the same text as in [listing.md](listing.md) ("Single purpose description").
- **Permission justifications:** the same texts as in listing.md for `clipboardRead`, `clipboardWrite`, `offscreen`,
  `alarms` and the host permission.
- **Remote code:** No.
- **Data usage:** tick "Authentication information" and "Website content", as for Edge.
- **Certifications:** the three "I certify" boxes are yours to tick (they are true: data goes only to the user's own server).
- **Privacy policy URL:** `https://privatepaste.ficdev.xyz/privacy.html`

## Distribution tab

Public, all regions.

## Test instructions

If the dashboard offers a test-instructions field, paste the "Notes for certification" block from listing.md (it fits
the same way; reviewers run their own local server with Docker).