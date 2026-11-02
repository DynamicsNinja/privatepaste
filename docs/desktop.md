# Desktop app

Install, use and build the PrivatePaste tray app for Windows, macOS and Linux.

## Desktop app

A tray app that keeps your clipboard in sync with the server (Windows; see [macOS and Linux](#macos-and-linux) for the other platforms).

- **Install**: download `PrivatePaste-<version>-win-x64.msi` from the latest GitHub Release and run it. It installs for your user only (no admin prompt) to `%LOCALAPPDATA%\Programs\PrivatePaste\PrivatePaste.exe`, adds a Start menu shortcut and can start the app when it finishes. No .NET runtime is needed.
- **Portable**: `PrivatePaste-<version>-win-x64.zip` holds the same single self-contained `PrivatePaste.exe`. Unzip it anywhere and run it; nothing is installed.
- **SmartScreen**: the installer and the exe are not code-signed yet (see [Code signing](../README.md#code-signing)), so Windows shows "Windows protected your PC". Click "More info", then "Run anyway", or before opening the download right-click it, choose Properties, tick **Unblock** and click OK.
- **Work PCs**: a managed PC can block unsigned apps with Microsoft Defender attack surface reduction (ASR) rules. Ask your IT department for an exception for the exact path `%LOCALAPPDATA%\Programs\PrivatePaste\PrivatePaste.exe`.
- **Upgrade**: install the newer `.msi`. It closes a running PrivatePaste, replaces the old version and keeps your settings, sign-in and history.
- **Silent upgrade**: after an upgrade run with `msiexec /qn` or `/passive` the app is not started again; it comes back at your next sign-in (if "Start with Windows" is on) or when you start it from the Start menu.
- **First sign-in**: start the app, enter the server URL (`https://` only; plain `http://` is accepted for localhost), your name, your password and a device name (on a new server the owner is `owner`, with the server's `PP_PASSWORD`). The app remembers the name. A wrong name or password shows "Wrong name or password." After that it stays signed in and starts minimized in the tray.
- **Unlock**: clips are end-to-end encrypted (see [Self-hosting](self-hosting.md#end-to-end-encryption)), so after signing in the app asks for the passphrase once and shows "Unlock your history". A wrong one shows "That passphrase doesn't unlock this server's history." On a server where encryption isn't set up yet, the owner's app shows "Set up encryption" with two passphrase fields and the rule "Use at least 12 characters. A short sentence works well."; anyone else's app shows "Ask the owner to set up encryption." and doesn't sync until the owner has done it. If you close that window, the tray says "Locked" (or "Encryption isn't set up") and its menu has **Unlock…** (or **Set up encryption…**) to open it again. The desktop app has no code scanner: it always unlocks with the passphrase. The key is then kept with the sign-in token, protected the same way, so the app doesn't ask again.
- **Settings > Encryption** (shown while the app is unlocked): **Change passphrase** (owner only) asks for the current passphrase and the new one twice, then re-wraps the key under the new one and says "Passphrase changed."; the history stays and devices that are already unlocked keep working. **Add a device** shows a QR code for the Android app to scan, so you don't type the passphrase on the phone ("Scan this with PrivatePaste on your phone. It works once, for 10 minutes."). Resetting encryption is in the web UI only.
- **After a reset**: when the owner resets encryption in the web UI, the app clears its history cache, thumbnails and unsent clips, and asks for the new passphrase with "Encryption was reset. Enter the new passphrase."
- **Updates needed**: if the server is older than the app, the app says "Update your PrivatePaste server to keep syncing."; if the app is older than the server, the tray tooltip and the popup say "Update PrivatePaste to keep syncing." A clip of a kind this version doesn't know (from a newer app) is listed as "Update PrivatePaste to see this clip" and can't be pasted.
- **Signing out** deletes the key from this computer along with the sign-in token, and so does the device being signed out from the People page.
- **Hotkey**: Ctrl+Shift+V opens the history popup. Pick an item to re-sync it, put it on the clipboard and paste it. The hotkey can be changed in Settings. Typing searches the clips kept on this computer (the latest 500); the server can't search, because it only holds ciphertext. When nothing kept matches, the list shows a single row, "Search older clips": Enter or a click on it looks through the rest of the history on the server, decrypting it here, and adds the matches as they are found ("Searching older clips…" while it runs).
- **Popup and focus**: on Windows the popup never takes the focus from the app you are in, so a text box stays in edit mode (Explorer's address bar or a file rename box too) and the paste lands there. While it is open, typing goes to its search box, Up/Down/Page Up/Page Down pick, Enter or a click pastes, Ctrl+P pins, Ctrl+1–5 filter, Ctrl+S saves files, and Esc, a click outside or switching to another window closes it. Other keys are ignored, so nothing you press can act on the app below. The search box takes direct keys only: IME input (Japanese, Chinese, ...) and dead-key accents can't be typed into it.
- **Pinned clips**: the popup has History and Pinned tabs (Tab switches between them). Each row has a pin button, and Ctrl+P pins or unpins the selected row. Pinned clips sync to every device, show a pin in History, and are kept when the history is cleared or auto-deleted on the server. The Pinned tab lists them all, even ones older than the 500 kept in the cache.
- **Filter by type**: under the tabs, the chips All, Text, Links, Images and Files show only clips of one kind; click a chip or press Ctrl+1 (All) to Ctrl+5 (Files). Choosing the active chip again goes back to All. The filter works together with the tabs and the search, "Search older clips" keeps it, and every time the popup opens it starts at All. It filters the clips kept on this computer (the latest 500, plus every pinned clip on the Pinned tab); with nothing of that kind it says "No files yet" (or "No text yet", "No links yet", "No images yet"; "No pinned files" and so on on the Pinned tab). The footer reads "Enter to paste   Tab for Pinned   Ctrl+P to pin   Ctrl+1–5 to filter".
- **Files**: see [Files](#files).
- **Private copies and the blocklist**: see [Private copies](#private-copies).
- **Pause**: the tray menu has Pause sync. While paused nothing is sent or received, and clips copied during the pause are never uploaded, not even after Resume.
- **Where data lives**: `%APPDATA%\PrivatePaste` holds the settings, the encrypted clip cache (latest 500 items), the DPAPI-protected sign-in token and vault key, received files in `files\` (see [Files](#files)), and `logs\`.
- **Uninstall**: open Settings > Apps > Installed apps, find PrivatePaste and choose Uninstall. It closes the app, removes it and its "Start with Windows" entry, and keeps `%APPDATA%\PrivatePaste`; delete that folder by hand to remove your history and sign-in too.
- **Uninstall the portable exe**: if you are signed in, untick "Start with Windows" in Settings; if you are signed out (Settings needs a sign-in), remove the autostart value as described in the next point instead. Then quit the app from the tray and delete `PrivatePaste.exe` and the `%APPDATA%\PrivatePaste` folder.
- **Leftover autostart entry**: if you deleted the portable exe without unticking "Start with Windows", remove the value `PrivatePaste` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (regedit, or `reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v PrivatePaste /f`).
- **Build it yourself**: `powershell -File scripts/publish-windows.ps1` writes `artifacts/win-x64/PrivatePaste.exe` and `artifacts/PrivatePaste-win-x64.zip`. `powershell -File scripts/build-installer.ps1 -Version 0.1.0` does the same and then writes `artifacts/PrivatePaste-0.1.0-win-x64.msi` (add `-NoPublish` to reuse the exe already built). The installer is made with the WiX Toolset, a .NET local tool pinned in `.config/dotnet-tools.json`; the script restores it, so only the .NET SDK is needed. The MSI version is the three numbers of the release version: a prerelease suffix is dropped, so `0.2.0-rc1` becomes MSI version `0.2.0`, and the final `0.2.0` installs over the release candidate. Pushing a `desktop-v*` tag runs `.github/workflows/desktop.yml`, which tests, builds all platforms and attaches them to a GitHub Release.

## Files

Copy files in Explorer (Ctrl+C) and they reach your other devices as one clip. Like every clip they are encrypted on this computer first: the server never sees the names or the contents.

- **What is sent**: up to 100 files and 25 MB in total per copy (the server's `PP_MAX_FILE_MB`; the message names its limit). Folders in the selection are skipped and the files next to them are sent. The app says why when it sends nothing, once per selection:
  - "Folders aren't synced." when only folders were copied.
  - "Too many files. Copy at most 100 at once."
  - "Files over 25 MB aren't synced."
  - "Update your PrivatePaste server to sync files." on a server that doesn't take files.
  - "Couldn't send the files. Copy them again when you're online." Unlike text and images, files are never queued to be sent later.
- **Images come first**: when an app puts an image and a list of files on the clipboard together (some browsers do for "Copy image"), the image is sent, not the files. A copy in Explorer carries no image, so it is always files. On macOS a Finder copy that also carries a PNG (`image/png`) syncs as that image (best effort, not yet verified on real machines).
- **Receiving**: files from another device are downloaded, decrypted and put on the clipboard as files, so Ctrl+V in Explorer pastes copies of them. The incoming-clip notification shows them like the popup does. Copying them again here doesn't send them back.
- **Names from other devices** are made safe before anything is written: folder parts, characters Windows doesn't allow and trailing dots are dropped, reserved names such as `CON` get a `_` in front, and a name is cut to 200 bytes. Two files with the same name become `report.pdf` and `report (2).pdf`. A download that fails or doesn't decrypt leaves no file behind.
- **Where they are kept**: in `%APPDATA%\PrivatePaste\files\`, one folder per clip, at most 500 MB in all (the oldest folders go first). A clip's files are removed when the clip leaves the history on this computer (deleted, cleared, auto-deleted, or older than the 500 kept), and all of them at sign-out and when encryption is reset. The files on the clipboard and files being downloaded or saved stay until that is done, even for a pinned clip or an older search hit that isn't in the history here.
- **In the popup**: a file row has a file icon and reads like `report.pdf +2 more · 3.4 MB` (the first name, how many more, the total size); search matches every file name. Enter pastes the files, downloading them first if they aren't here yet; a download that takes more than a few seconds is never cut off, and when it is done the files are copied ("Copied") instead of pasted. While a file row is selected the footer adds "Ctrl+S to save": Ctrl+S copies the files into your Downloads folder and says "Saved report.pdf to Downloads" or "Saved 3 files to Downloads". A name that is already there gets ` (2)`, so nothing is overwritten. Saving doesn't take the focus either. If it fails it says "Couldn't save the files."
- **While paused**: "Sync is paused. Resume it to paste files." for a file clip, and "Sync is paused. Resume it to save files." for files that would need a download; files already here can still be saved.
- **Logs** never hold file names or contents.
- **macOS and Linux**: the app reads and writes the file list as file URLs (`public.file-url` on macOS; `text/uri-list` and `x-special/gnome-copied-files` on Linux), and Save uses `~/Downloads`. Like the private-copy markers, this is best effort and not yet verified on real machines.

## Private copies

A copy that the app it came from marks as private is ignored completely: it is not uploaded, not put in the history or cache, and not sent to any device. The same goes for copies from apps on your blocklist (Settings > Private copies). Only a line such as "skipped a copy marked private" is logged, never the content or its length. Text that merely looks like a password is not guessed at and syncs normally.

What is checked, by platform:

| Platform | Marker | Status |
|---|---|---|
| Windows | The clipboard formats `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore`, and `CanIncludeInClipboardHistory` / `CanUploadToCloudClipboard` holding 0 | The marker rules are unit-tested in every run. Reading the real clipboard (markers and source app) has automated tests that run only with `PP_CLIPBOARD_TESTS=1`, because they overwrite the clipboard |
| macOS | The pasteboard types `org.nspasteboard.ConcealedType` and `org.nspasteboard.TransientType` | Best effort, not verified on a real Mac |
| Linux | The `x-kde-passwordManagerHint` target with the value `secret` | Best effort, not verified on a real machine |

On macOS and Linux the app reads the clipboard through Avalonia, which may not expose these marker types, so detection there may not work at all until it has been tried on real machines. Don't rely on it for passwords on those systems; use the blocklist and check that it does what you expect. On Windows, a marker the app can't read (for example a DWORD it can't parse) counts as private: the copy is skipped rather than risked.

**The blocklist** covers apps that don't mark their copies. In Settings > Private copies pick an app from the ones you recently copied from, or type its name (like `keepassxc`), and copies from it are ignored.

- On Windows the app is the process that owns the clipboard (or the foreground app when the owner is unknown).
- On macOS the app is the frontmost one when the copy is read, so a copy made just before you switched apps can be attributed to the wrong app.
- On Linux the blocklist works on X11 only; on Wayland the copying app can't be identified, so the setting is replaced by a note. Even on X11 it may not match when a clipboard manager (klipper, GPaste) owns the clipboard. The name can also be a runtime (`python3`, `electron`) or a Flatpak wrapper (`bwrap`) instead of the app you expect.
- Copies made by a password manager's **browser extension** come from the browser, so the blocklist can't target them (blocking the browser would block everything). They are skipped only if the extension marks them.

How the common password managers behave on Windows is still being tested. Each cell will say *Skipped automatically*, *Add it to the blocklist* or *Can't be caught*.

| Password manager | Desktop app | Browser extension |
|---|---|---|
| Bitwarden | Not tested yet | Not tested yet |
| 1Password | Not tested yet | Not tested yet |
| KeePass / KeePassXC | Not tested yet | Not tested yet |
| LastPass | Not tested yet | Not tested yet |

Android is different: the app only reads the clipboard when you send it, so it sends what you chose.

## macOS and Linux

The desktop app also runs on macOS and Linux. These ports are best-effort: they are built in CI but have had less real-world testing than Windows.

**macOS** (Apple Silicon and Intel, `PrivatePaste-<version>-osx-arm64.zip` / `osx-x64.zip`)

- The app is not signed or notarized. Unzip it, then right-click `PrivatePaste.app` and choose Open, or run `xattr -d com.apple.quarantine PrivatePaste.app`.
- Grant Accessibility access (System Settings > Privacy & Security > Accessibility) so the global hotkey (Cmd+Shift+V) and paste work. After an update macOS can drop the grant because the app is unsigned; remove and re-add it if the hotkey stops working.
- The sign-in token and the vault key are encrypted with a random key kept in your login keychain (service `PrivatePaste`). The key is handed to the `security` tool on stdin, so it is not visible in the process list. While the app reads it, other programs run by your user can ask the keychain for it too (macOS prompts).
- Autostart is a LaunchAgent at `~/Library/LaunchAgents/xyz.ficdev.privatepaste.plist`.

**Linux** (`PrivatePaste-<version>-linux-x64.tar.gz`)

- Unpack it and run `./install.sh` for a launcher in your application menu, or just run `PrivatePaste/PrivatePaste`.
- An X11 session works best. On Wayland the clipboard and global hotkey are limited by the compositor; the app tells you once.
- The key that protects the sign-in token and the vault key is stored through the Secret Service. Install `secret-tool` (package `libsecret-tools` on Debian/Ubuntu, `libsecret` on Fedora/Arch) and have a keyring running (GNOME Keyring, KWallet's Secret Service). Without it the app falls back to a key file with mode 600 in `~/.config/privatepaste/` and says so once; that guards against other users, not other programs of yours. If the keyring is locked or unavailable at start, the app shows the sign-in screen and writes the error to its log (`~/.config/privatepaste/logs/`); unlock the keyring and restart it to pick up your sign-in and history. Starting never replaces a key it cannot reach; signing in again does (that sign-in starts a fresh history anyway). macOS behaves the same when the keychain is locked.
- Autostart is `~/.config/autostart/privatepaste.desktop`.
