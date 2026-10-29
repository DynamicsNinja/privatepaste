# Desktop app

Install, use and build the PrivatePaste tray app for Windows, macOS and Linux.

## Desktop app

A tray app that keeps your clipboard in sync with the server (Windows; see [macOS and Linux](#macos-and-linux) for the other platforms).

- **Install**: download `PrivatePaste-<version>-win-x64.msi` from the latest GitHub Release and run it. It installs for your user only (no admin prompt) to `%LOCALAPPDATA%\Programs\PrivatePaste\PrivatePaste.exe`, adds a Start menu shortcut and can start the app when it finishes. No .NET runtime is needed.
- **Portable**: `PrivatePaste-<version>-win-x64.zip` holds the same single self-contained `PrivatePaste.exe`. Unzip it anywhere and run it; nothing is installed.
- **SmartScreen**: the installer and the exe are not code-signed, so Windows shows "Windows protected your PC". Click "More info", then "Run anyway".
- **Work PCs**: a managed PC can block unsigned apps with Microsoft Defender attack surface reduction (ASR) rules. Ask your IT department for an exception for the exact path `%LOCALAPPDATA%\Programs\PrivatePaste\PrivatePaste.exe`.
- **Upgrade**: install the newer `.msi`. It closes a running PrivatePaste, replaces the old version and keeps your settings, sign-in and history. Installing an older version over a newer one is refused; uninstall first if you really want to go back.
- **Silent upgrade**: after an upgrade run with `msiexec /qn` or `/passive` the app is not started again; it comes back at your next sign-in (if "Start with Windows" is on) or when you start it from the Start menu.
- **First sign-in**: start the app, enter the server URL (`https://` only; plain `http://` is accepted for localhost), the server password and a device name. After that it stays signed in and starts minimized in the tray.
- **Hotkey**: Ctrl+Shift+V opens the history popup. Pick an item to re-sync it, put it on the clipboard and paste it. The hotkey can be changed in Settings.
- **Popup and focus**: on Windows the popup never takes the focus from the app you are in, so a text box stays in edit mode (Explorer's address bar or a file rename box too) and the paste lands there. While it is open, typing goes to its search box, Up/Down/Page Up/Page Down pick, Enter or a click pastes, and Esc, a click outside or switching to another window closes it. Other keys are ignored, so nothing you press can act on the app below. The search box takes direct keys only: IME input (Japanese, Chinese, ...) and dead-key accents can't be typed into it.
- **Pause**: the tray menu has Pause sync. While paused nothing is sent or received, and clips copied during the pause are never uploaded, not even after Resume.
- **Where data lives**: `%APPDATA%\PrivatePaste` holds the settings, the encrypted clip cache (latest 500 items), the DPAPI-protected sign-in token and `logs\`.
- **Uninstall**: open Settings > Apps > Installed apps, find PrivatePaste and choose Uninstall. It closes the app, removes it and its "Start with Windows" entry, and keeps `%APPDATA%\PrivatePaste`; delete that folder by hand to remove your history and sign-in too.
- **Uninstall the portable exe**: if you are signed in, untick "Start with Windows" in Settings; if you are signed out (Settings needs a sign-in), remove the autostart value as described in the next point instead. Then quit the app from the tray and delete `PrivatePaste.exe` and the `%APPDATA%\PrivatePaste` folder.
- **Leftover autostart entry**: if you deleted the portable exe without unticking "Start with Windows", remove the value `PrivatePaste` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (regedit, or `reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v PrivatePaste /f`).
- **Build it yourself**: `powershell -File scripts/publish-windows.ps1` writes `artifacts/win-x64/PrivatePaste.exe` and `artifacts/PrivatePaste-win-x64.zip`. `powershell -File scripts/build-installer.ps1 -Version 0.1.0` does the same and then writes `artifacts/PrivatePaste-0.1.0-win-x64.msi` (add `-NoPublish` to reuse the exe already built). The installer is made with the WiX Toolset, a .NET local tool pinned in `.config/dotnet-tools.json`; the script restores it, so only the .NET SDK is needed. The MSI version is the three numbers of the release version: a prerelease suffix is dropped, so `0.2.0-rc1` becomes MSI version `0.2.0`, and the final `0.2.0` installs over the release candidate. Pushing a `desktop-v*` tag runs `.github/workflows/desktop.yml`, which tests, builds all platforms and attaches them to a GitHub Release.

## macOS and Linux

The desktop app also runs on macOS and Linux. These ports are best-effort: they are built in CI but have had less real-world testing than Windows.

**macOS** (Apple Silicon and Intel, `PrivatePaste-<version>-osx-arm64.zip` / `osx-x64.zip`)

- The app is not signed or notarized. Unzip it, then right-click `PrivatePaste.app` and choose Open, or run `xattr -d com.apple.quarantine PrivatePaste.app`.
- Grant Accessibility access (System Settings > Privacy & Security > Accessibility) so the global hotkey (Cmd+Shift+V) and paste work. After an update macOS can drop the grant because the app is unsigned; remove and re-add it if the hotkey stops working.
- The sign-in token is encrypted with a random key kept in your login keychain (service `PrivatePaste`). The key is handed to the `security` tool on stdin, so it is not visible in the process list. While the app reads it, other programs run by your user can ask the keychain for it too (macOS prompts).
- Autostart is a LaunchAgent at `~/Library/LaunchAgents/xyz.ficdev.privatepaste.plist`.

**Linux** (`PrivatePaste-<version>-linux-x64.tar.gz`)

- Unpack it and run `./install.sh` for a launcher in your application menu, or just run `PrivatePaste/PrivatePaste`.
- An X11 session works best. On Wayland the clipboard and global hotkey are limited by the compositor; the app tells you once.
- The sign-in token key is stored through the Secret Service. Install `secret-tool` (package `libsecret-tools` on Debian/Ubuntu, `libsecret` on Fedora/Arch) and have a keyring running (GNOME Keyring, KWallet's Secret Service). Without it the app falls back to a key file with mode 600 in `~/.config/privatepaste/` and says so once; that guards against other users, not other programs of yours. If the keyring is locked or unavailable at start, the app shows the sign-in screen and writes the error to its log (`~/.config/privatepaste/logs/`); unlock the keyring and restart it to pick up your sign-in and history. Starting never replaces a key it cannot reach; signing in again does (that sign-in starts a fresh history anyway). macOS behaves the same when the keychain is locked.
- Autostart is `~/.config/autostart/privatepaste.desktop`.
