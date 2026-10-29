# PrivatePaste

Self-hosted clipboard sync: an ASP.NET Core server with a web UI, an Avalonia desktop tray app (Windows, macOS, Linux) and a Kotlin Android app. The product spec is `docs/superpowers/specs/2026-10-28-privatepaste-design.md`; the visual design system is `DESIGN.md`; user docs are `README.md` and `docs/`.

## Layout

| Path | What |
|---|---|
| `src/PrivatePaste.Protocol` | Shared wire types (`ClipDto`, ...) and `ContentHash`. Changes here are protocol changes. |
| `src/PrivatePaste.Server` | REST API, WebSocket hub (`Realtime/`), encrypted SQLite storage (`Storage/`, migrations in `PrivatePasteDb`), Razor Pages web UI (`Pages/`, `wwwroot/`). |
| `src/PrivatePaste.Client.Core` | Desktop sync logic, UI-free: `Sync/SyncEngine`, `ClipboardCoordinator`, `Cache/HistoryCache`, settings. |
| `src/PrivatePaste.Desktop` | Avalonia app: tray, popup, toast, settings; theme in `Theme/NightVault.axaml`; OS specifics in `Platform/`. |
| `tests/PrivatePaste.Tests` | xUnit tests for all .NET projects, including headless Avalonia renders (`Client/DesktopRenderTests.cs`). |
| `android/` | Gradle project, package `xyz.ficdev.privatepaste` (`api`, `sync`, `service`, `clipboard`, `send`, `storage`, `ui`). Port of the desktop sync engine. |
| `installer/PrivatePaste.wxs` | Per-user WiX MSI. |
| `branding/` | Logo, lockups, app icon, platform icons, brand guide. |
| `scripts/` | Publish, installer, Android keystore, lockup and README-image scripts. |

## Commands

- Build and test .NET: `dotnet test` (from the repo root). `TreatWarningsAsErrors` is on: a warning fails the build.
- Run the server locally: `PP_DB_KEY=0123456789abcdef0123 PP_PASSWORD=dev PP_DATA_DIR=./.devdata dotnet run --project src/PrivatePaste.Server`. The launch profile picks the port (http://localhost:5202) and ignores `ASPNETCORE_URLS`; for another port add `--no-launch-profile --urls http://localhost:<port>`.
- Android tests: in `android/`, `./gradlew testDebugUnitTest` (Robolectric, sdk 35). Needs a JDK 21 in `JAVA_HOME`; on this machine `C:\Program Files\Microsoft\jdk-21.0.13.21-hotspot`.
- Android end-to-end against a local server: `PP_E2E_URL=http://localhost:5202 ./gradlew testDebugUnitTest --tests '*LocalServerE2eTest*'`.
- Screenshots for review: set `PP_SCREENSHOT_DIR` and run `DesktopRenderTests` (desktop) or `ScreenshotTest` (Android); PNGs land in that directory.
- README hero images: `powershell -File scripts/readme/build.ps1` (see `scripts/readme/README.md`, and the README rule below for when).

## Rules

- **Never use the live server** (https://paste.ficdev.xyz) or its password for testing, e2e or screenshots. Use a local server with `PP_PASSWORD=dev` and a throwaway `PP_DATA_DIR`.
- Do not touch the owner's installed desktop app or `%APPDATA%\PrivatePaste`, and do not change Defender or other security settings: tell the owner what to run instead.
- Secrets stay out of the repo and out of chat: `PP_DB_KEY`, the Android keystore (`android/keystore/`, git-ignored) and its password.
- **Pushing `main` deploys the server.** Dokploy redeploys https://paste.ficdev.xyz on every push to `main`, including its startup DB migration. Before pushing a schema change, ask the owner to back up `/data`.
- Protocol changes must stay compatible both ways: add fields as optional, never rename or remove. Android parses with `ignoreUnknownKeys` and `explicitNulls = false`; a field that can be missing or null on the wire must be nullable with a default there (a missing `sourceDeviceId` once broke Android sync).
- `ContentHash` must match on every client: keep the test vectors in `docs/api.md` passing on .NET and Android.
- Platform strings are `windows`, `macos`, `linux`, `android`, `web`; anything else is shown as an unknown device.
- **Keep the README in step with the apps.** A change is not done until the README matches it (details in `scripts/readme/README.md`):
  - Changed how the web history page, the desktop history popup or the Android history screen looks (layout, colors, type, icons, row content, top bar)? Run `powershell -File scripts/readme/build.ps1`, look at both `docs/images/hero-*.png` and commit them with the change.
  - Changed the logo, wordmark or brand colors? Also run `python scripts/make-lockup.py .` and update `DESIGN.md` and `branding/README.md`.
  - Changed features, platforms, release file names, environment variables, the port, security behaviour or install steps? Update the matching README text and `docs/` page; the list of facts to check is under "Text to keep in step" in `scripts/readme/README.md`.
  - If the build can't run (no JDK, no Node), say so in your summary instead of leaving the old images silently.

## Gotchas

- Web assets sit behind Cloudflare: reference CSS/JS with `asp-append-version`, and bump the `?v=N` on font URLs in `site.css` when a font file changes.
- The server allows 5 sign-ins per minute per IP; scripts that create several devices must stay under that.
- Private-copy markers (`Platform/PrivateCopyMarkers.cs`): Windows `ExcludeClipboardContentFromMonitorProcessing` and `Clipboard Viewer Ignore` mark a copy by existing, `CanIncludeInClipboardHistory` / `CanUploadToCloudClipboard` by a DWORD 0; macOS `org.nspasteboard.ConcealedType` / `TransientType`; Linux the `x-kde-passwordManagerHint` target holding `secret`. A marker that is present but unreadable fails closed (the copy is skipped). macOS and Linux detection is best effort and unverified on real machines.
- A backfilled upload copied before the last history clear, or older than the auto-delete age, gets 410 "Cleared"; clients drop such queued clips quietly instead of retrying (`docs/api.md`).
- Pinning never changes `lastCopiedAt`: catch-up and the "apply only the newest live clip to the clipboard" rules depend on it, so a pin arrives as a `live: false` change with a new `seq` and must not be mistaken for a copy.
- Desktop title-bar tests run on Windows only (Linux and macOS keep native decorations).
- Android Robolectric tests use the FileProvider stand-in on every host (androidx FileProvider can't map Robolectric's cache dir).
- `SyncEngineTests` has a known rare flake on the Linux runner: the server registers a new WebSocket just after the client sees the upgrade, so a clip sent in that instant misses the live push. Rerun; a real fix belongs in the hub/connect handshake.
- MSI: never change the `UpgradeCode`. `RemoveExistingProducts` runs after `InstallValidate` and the close-app action after `InstallInitialize`; reordering brings back error 2613. Test installer changes with a real install, a repair and an upgrade over the running app.
- Managed Windows PCs may block the unsigned exe via a Defender ASR rule; the fix is an exception the owner adds, not a code change.
- The Windows popup never takes the focus (`PopupWindow.KeepsFocusBelow`): it is WS_EX_NOACTIVATE and the keyboard hook hands it the keys (`HotkeyService.Capture`, `PopupKeyRouter`). Taking the focus ends editing in Explorer's address bar, rename boxes and inline editors, so the paste lands nowhere; don't bring back `Activate()` there. Nothing in it may be focusable.
- Focus behaviour can't be scripted: Windows doesn't hand keys injected by another process to the hook. Test by hand with an isolated build (`--data-dir`) against a local server, with the installed app quit so only one hook runs. The `popup +Nms:` lines in that run's log trace each step. **Never log key presses or typed text**: the hook sees everything the user types.

## Releases

Each is a tag push; CI tests, builds and creates the GitHub Release:

| Tag | Workflow | Output |
|---|---|---|
| `desktop-vX.Y.Z` | `.github/workflows/desktop.yml` | Windows MSI and zip, macOS arm64/x64 zips, Linux tar.gz |
| `android-vX.Y.Z` | `.github/workflows/android.yml` | Signed APK (keystore from repo secrets) |
| `server-vX.Y.Z` | `.github/workflows/server.yml` | `ghcr.io/dynamicsninja/privatepaste:X.Y.Z` and `latest` (linux/amd64, linux/arm64) |

A version with a `-` (e.g. `0.2.0-rc1`) is a prerelease. The MSI version drops the suffix, so an RC and its final share a version. When a release changes the server image, update the image tag in `deploy/dokploy/privatepaste/` too.

## Conventions

- Match the surrounding code: comment density, naming and idiom. Comments explain why, not what.
- Tests come with every behaviour change; UI changes get a render/screenshot check in both themes.
- UI copy: plain, sentence case, says what happens ("Copy", "Copied", "Sign in to PrivatePaste first"). See `DESIGN.md`.
