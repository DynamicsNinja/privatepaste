# Android app

Install, use and build the PrivatePaste Android app.

## Android app

A sideloaded Android app (Android 10 or newer) that puts clips from your other devices on the phone's clipboard, sends the phone's clipboard or a shared item to them, and shows the searchable history. It talks only to your server: no Google Play, no push service, no analytics.

- **Install**: download `PrivatePaste-android-<version>.apk` from the latest GitHub Release on the phone, open it, and allow "Install unknown apps" for the browser or file manager you used when Android asks.
- **Sign in**: enter the server URL (`https://` only; plain `http://` works only for localhost, for development), the server password and a device name. After that the app stays signed in.
- **Allow notifications and background use**: after the first sign-in the app asks for the notification permission (the service shows a permanent "PrivatePaste connected" notification) and to be exempt from battery optimization. Allow both. Without them Android stops the connection when the phone sleeps.
- **Send clipboard tile**: pull down Quick Settings, tap the edit (pencil) button, and drag "Send clipboard" into the active tiles. Tapping it sends what is on the clipboard now.
- **Share to PrivatePaste**: in any app, use Share and pick "Send to PrivatePaste" to send text, a link or an image.
- **History**: the app lists the latest 200 clips with search. Tap a clip to put it on the clipboard. Long-press a clip for a menu with Pin (or Unpin) and Delete. Deleting asks first, and the clip disappears on your other devices too.
- **Pinned**: the Pinned tab lists the clips you pinned on any device, most recently pinned first, even ones older than the 200 in History. Pinned clips also show a pin in History. Pinning doesn't move a clip or put it on the clipboard. Pinned clips are kept when the history is cleared or auto-deleted on the server (both are set in the web UI).
- **Private copies**: the Android app only reads the clipboard when you send it (the tile, Share), so it sends what you chose, even from a password manager. The desktop apps are the ones that skip private copies.
- **Delivery** (Settings): "Copy automatically" writes incoming clips straight to the clipboard and is the default. Android may refuse clipboard writes from a background app, mostly while the phone is locked or another app is in front. If clips from your other devices don't land on the clipboard in that case, switch to "Show a notification with a Copy button": each incoming clip posts a notification, and tapping Copy puts it on the clipboard.
- **Battery settings (Xiaomi, Samsung, Huawei and others)**: these phones stop background apps more aggressively than stock Android. Open Settings → Apps → PrivatePaste → Battery and choose "Unrestricted" (the wording differs by maker). Also:
  - **Xiaomi (MIUI / HyperOS)**: set Battery saver to "No restrictions" for the app and turn on Autostart for it in Security or Permissions.
  - **Samsung (One UI)**: remove PrivatePaste from "Sleeping apps" and "Deep sleeping apps" (Settings → Battery → Background usage limits).
  - **Huawei (EMUI / HarmonyOS)**: in the app's Battery settings turn off "Manage automatically" and allow Auto-launch, Secondary launch and Run in background.
- **Revoked device**: if the phone is revoked from the web UI, the app shows a "Signed out" notification and stops. Sign in again to resume.
- **Updating**: install a newer APK over the old one. Android only accepts it when it is signed with the same key as the installed app, so keep using the release APKs from this repository's Releases. The first time you switch between a debug build and a release build you must uninstall the old one.
- **Release** (for the repository owner): run `powershell -File scripts/android-keystore.ps1`. It creates `android/keystore/release.jks` (git-ignored) and prints the password once and the `gh secret set` commands for the four secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`. Run them, then push a tag such as `android-v0.1.0`. `.github/workflows/android.yml` tests, builds the signed APK and attaches it to a GitHub Release (a version with a `-`, like `0.2.0-rc1`, becomes a prerelease). Back up `release.jks`: without it, updates can't be installed over the existing app.
- **Build it yourself**: in `android/`, run `./gradlew assembleDebug`. `./gradlew assembleRelease` signs only when `PP_ANDROID_KEYSTORE` (path), `PP_ANDROID_KEYSTORE_PASSWORD`, `PP_ANDROID_KEY_ALIAS` and `PP_ANDROID_KEY_PASSWORD` are set; otherwise it produces an unsigned APK and warns. `-PppVersion=0.1.0 -PppVersionCode=1` set the version.
- **End-to-end test**: with a local server running (see [Local development](self-hosting.md#local-development), `PP_PASSWORD=dev`, e.g. `ASPNETCORE_URLS=http://localhost:5299`), run `PP_E2E_URL=http://localhost:5299 ./gradlew testDebugUnitTest --tests '*LocalServerE2eTest*'` in `android/`. Without `PP_E2E_URL` the test is skipped.
