# Edge extension

Install and use the PrivatePaste extension for Microsoft Edge, for computers where the desktop app can't be installed.

## What it does

An extension (Manifest V3) that keeps the browser computer's clipboard in sync with your server, like the desktop app does, and shows the history in a toolbar window. It talks only to your server.

- **What it does**: copies you make in any app are encrypted and sent on their own while Edge is running. Clips from your other devices arrive live: text and links go on the clipboard. Images and files go to the history only; **Copy** on an image row in the toolbar window puts it on the clipboard, and a file row has **Download**. Files are never put on the clipboard.
- **Install from Edge Add-ons** (coming soon): PrivatePaste is being submitted to Microsoft Edge Add-ons. Once it is listed, that will be the way to install it, with updates arriving on their own. Until then, load it unpacked as below.
- **Install unpacked**: download `privatepaste-extension-<version>.zip` from the newest `extension-v` release on GitHub (or use the Edge extension download on the homepage) and unzip it to a folder you will keep (Edge reads the extension from there). Open `edge://extensions`, turn on **Developer mode**, choose **Load unpacked** and pick the unzipped folder. Then pin PrivatePaste from the puzzle-piece menu so its icon sits on the toolbar.
- **Privacy**: the extension sends data only to the server you sign in to, end-to-end encrypted, with no analytics; nothing goes to the developer. The [privacy policy](https://privatepaste.ficdev.xyz/privacy.html) covers the extension and the apps.
- **Sign in**: click the toolbar icon; while signed out it opens the sign-in form in a tab. Enter the server URL (`https://` only; plain `http://` works only for localhost, for development), your name, your password and a device name (default `Edge`; an extension can't read the computer's name, so change it if you have several). On a new server the owner is `owner`, with the server's `PP_PASSWORD`. The first sign-in to a server asks you to allow the extension to reach it; choose **Allow**. If the sign-in fails, the form says why ("Wrong name or password.", "Can't reach the server.", ...). After signing in you unlock in the same tab, then use the toolbar icon as usual.
- **Unlock**: clips are end-to-end encrypted (see [Self-hosting](self-hosting.md#end-to-end-encryption)), so after signing in the extension asks for the passphrase. Unlocking works with the passphrase only; there is no code to scan. It stays unlocked across Edge restarts: the key is kept in your Edge profile as a key that scripts, the extension's own included, can't export. The device's sign-in token is kept in the profile too, as it is. Like any browser data, both are only as safe as your Windows account: someone who can read your Edge profile's files can recover them. Signing out, or the device being signed out from the People page, deletes the key, the sign-in, the cached history and unsent copies.
- **Status**: the toolbar window shows `Syncing`, `Paused`, `Offline` or `Locked`. The **Pause syncing** switch stops sending and receiving, and the extension doesn't read the clipboard while paused, locked or signed out; copies made meanwhile are never uploaded, not even after you turn Pause off.
- **Device**: on the web UI's People page and next to each clip the extension shows as a "Browser extension" device.
- **Updates needed**: on a server older than the extension it shows "Update your PrivatePaste server to keep syncing."; on a server newer than the extension, "Update PrivatePaste to keep syncing." A clip of a kind this version doesn't know shows "Update PrivatePaste to see this clip" and is never put on the clipboard.
- **After a reset**: when the owner resets encryption in the web UI, the extension clears its cache and asks for the new passphrase with "Encryption was reset. Enter the new passphrase."
- **Live updates**: they need a server from the release that added the extension, because the browser can't send an `Authorization` header on a WebSocket and the server accepts the token in `Sec-WebSocket-Protocol` instead (see the [API reference](api.md)). With an older server the extension still works but checks for new clips every 2 seconds.

## What it can't do

- **It syncs only while Edge is running.** Close Edge and nothing is sent or received; copies made in the meantime are not sent when Edge starts again.
- **Copies from password managers sync too.** The extension can't tell them apart from other copies, unlike the desktop app (see [Private copies](desktop.md#private-copies)). Use **Pause syncing** before copying a secret, or use the desktop app. The settings say: "Copies from password managers sync too: the extension can't tell them apart."
- **Files copied in Explorer are not sent.** Only text and images on the clipboard are. Files from other devices arrive in the history and are downloaded from their row.
- **Large copies are skipped** when they are over the server's limits (text size, image size, 50 megapixels), and the status says why.
- **Edge only.** Chrome and Firefox are not supported.

## Store listing

Everything for the Edge Add-ons submission is in `extension/store/`: the texts to paste ([listing.md](../extension/store/listing.md), with a justification per permission and the reviewers' notes), the images and the script that makes them, and the step-by-step for publishing and updating ([README.md](../extension/store/README.md)). The one sentence for the `clipboardRead` permission:

> PrivatePaste reads what you copy so it can send it, encrypted, to your own server and your other devices.

## Build it yourself

`node extension/build.mjs [--version X.Y.Z] [--out <dir>]` writes the unpacked extension to `extension/dist/` (see `extension/README.md`). Unit tests: `node --test "extension/test/*.test.mjs"`.
