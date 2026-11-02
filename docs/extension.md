# Edge extension

Install and use the PrivatePaste extension for Microsoft Edge, for computers where the desktop app can't be installed.

## Edge extension

An extension (Manifest V3) that keeps the browser computer's clipboard in sync with your server, like the desktop app does, and shows the history in a toolbar window. It talks only to your server.

- **What it does**: copies you make in any app are encrypted and sent on their own while Edge is running. Clips from your other devices arrive live: text and links go on the clipboard. Images and files go to the history only; **Copy** on an image row in the toolbar window puts it on the clipboard, and a file row has **Download**. Files are never put on the clipboard.
- **Install**: download `privatepaste-extension-<version>.zip` from the latest GitHub Release and unzip it to a folder you will keep (Edge reads the extension from there). Open `edge://extensions`, turn on **Developer mode**, choose **Load unpacked** and pick the unzipped folder. Then pin PrivatePaste from the puzzle-piece menu so its icon sits on the toolbar. Edge Add-ons will be another way to install it later.
- **Sign in**: click the toolbar icon and enter the server URL (`https://` only; plain `http://` works only for localhost, for development), your name, your password and a device name (default `Edge`; an extension can't read the computer's name, so change it if you have several). On a new server the owner is `owner`, with the server's `PP_PASSWORD`.
- **Unlock**: clips are end-to-end encrypted (see [Self-hosting](self-hosting.md#end-to-end-encryption)), so after signing in the extension asks for the passphrase. Unlocking works with the passphrase only; there is no code to scan. It stays unlocked across Edge restarts: the key is kept in the browser as a key that can't be exported, not as readable bytes. Signing out, or the device being signed out from the People page, deletes the key, the sign-in, the cached history and unsent copies.
- **Status**: the toolbar window shows `Syncing`, `Paused`, `Offline` or `Locked`. The **Pause syncing** switch stops sending and receiving; copies made while paused are never uploaded.
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

The one sentence for the `clipboardRead` permission:

> PrivatePaste reads what you copy so it can send it, encrypted, to your own server and your other devices.

## Build it yourself

`node extension/build.mjs [--version X.Y.Z] [--out <dir>]` writes the unpacked extension to `extension/dist/` (see `extension/README.md`). Unit tests: `node --test "extension/test/*.test.mjs"`.

## Release

For the repository owner: push a tag such as `extension-v0.1.0`. `.github/workflows/extension.yml` runs the tests, builds with the version from the tag, zips the result as `privatepaste-extension-<version>.zip` and attaches it to a GitHub Release (a version with a `-`, like `0.2.0-rc1`, becomes a prerelease).
