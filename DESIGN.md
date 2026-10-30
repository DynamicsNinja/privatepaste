# PrivatePaste design system: Night vault

Your own vault for everything you copy: a deep violet night with a soft lilac glow. Dark is the default; light mode swaps to pale lavender with a deeper violet. One look across the web UI, the desktop app and the Android app.

This file is the source of truth for the look. `branding/README.md` covers the brand assets; this file covers how the apps use them. When a value changes, change it on every platform listed under [Where it lives](#where-it-lives).

## Principles

- **Quiet surfaces, one glow.** Panels are flat and close in tone. Only the newest clip glows (accent border plus a soft 3px ring); nothing else does.
- **Content first.** A clip row shows the clip itself, then who copied it and when. Chrome stays muted.
- **Destructive is quiet.** Delete and sign out are muted text until hover or focus, when they turn `danger`.
- **Same row everywhere.** The clip row (kind tile, text, platform icon, device, age) reads the same on web, desktop and Android.

## Color

| Token | Dark | Light | Role |
|---|---|---|---|
| `night` | `#15122B` | `#F6F4FD` | Page and window background |
| `surface` | `#1D1838` | `#FFFFFF` | Rows, panels, cards |
| `raised` | `#262046` | `#EFEBFC` | Inputs, kind tiles, active nav, secondary buttons |
| `line` | `#2E2756` | `#E1DBF7` | Borders, dividers |
| `text` | `#E9E6F7` | `#1E1A38` | Body text |
| `muted` | `#9A94BD` | `#625C88` | Secondary text, meta lines, platform icons |
| `accent` | `#B7A5FF` | `#5B45D6` | Links, primary button, focus, newest-clip glow |
| `on-accent` | `#15122B` | `#FFFFFF` | Text on an accent fill |
| `accent-hover` | `#C6B8FF` | `#4A36C0` | Primary button hover (desktop) |
| `danger` | `#FF8FA3` | `#C2364A` | Destructive actions, on hover/focus only; errors |
| `live` | `#7CE0C3` | `#7CE0C3` | "Connected" dot (apps only) |

Derived web values: `accent-soft` is the accent at 14% (dark) / 10% (light) for hover fills; `danger-soft` is danger at 12% / 8%; the glow is a 3px ring of the accent at 16% / 14%.

The logo is fixed in both modes: tile `#221D40`, keyhole `#B7A5FF`, hairline edge `#B7A5FF` at 35%.

## Type

- **Sora**, self-hosted everywhere (SIL OFL, `src/PrivatePaste.Server/wwwroot/fonts/OFL.txt`). No third-party font service.
- Weights: 300 for the wordmark accent only, 400 for body, 600 for headings, buttons and the wordmark.
- **Wordmark:** `private` in Sora 600 in `text`, followed directly by `paste` in Sora 300 in `accent`; lowercase, no space. Where the font can't load (the README, external sites) use the outlined `branding/lockup-on-dark.svg` / `lockup-on-light.svg`, made by `scripts/make-lockup.py`.

## Shape and spacing

- Radius: 14px for rows and panels, 10px for controls (inputs, buttons, kind tiles), fully rounded for nav items, pills and the connection chip.
- Borders are 1px `line`. The newest clip's border is `accent`, plus the glow.
- Clip rows stack with an 8–10px gap; inner padding about 14px.

## Icons

- **Kind icons** (text, link, image) sit in a `raised` tile with a 10px radius, stroked in `accent`.
- **Platform icons** (`branding/platform-icons/`) are 24×24 single stroked paths (stroke 2, round caps and joins), drawn at 14px (web) / small inline size (apps) in `muted`, before the device name: `windows` (four squares), `macos` (⌘, not the Apple logo), `linux` (`>_`), `android` (robot head), `web` (globe), `unknown` (monitor, labelled "Unknown device") for any other or missing platform.
- **UI icons** (`branding/ui-icons/`): `pin.svg`, `copy.svg`, `trash.svg` and `check.svg`, each a 24×24 single stroked path like the platform icons. A web history row's actions are compact icon buttons with the name in `aria-label` and the tooltip: Copy (in `accent` on an `accent`-soft fill; the check replaces the copy icon for a moment once it worked), Pin/Unpin (ghost; filled, in `accent`, when the clip is pinned) and Delete (ghost trash; `danger` on hover). Pinned rows in History also carry a small `accent` pin before the device name.
- The SVG files are the source; each platform carries a copy of the path data, and tests fail if a copy drifts.

## Components

- **Clip row:** kind tile · first line of the text (one line, ellipsis) or the image thumbnail · meta line `[platform icon] Device, 5 min ago` in `muted`. Web and desktop popup show Copy (secondary button) and Delete (quiet); Android copies on tap and deletes on long-press.
- **Newest clip:** accent border and glow; on Android also a `live` dot at the row's end.
- **Search field:** `raised` fill, 10px radius, placeholder "Search history".
- **Connection chip (apps):** pill in `raised` with a `live` dot and "Connected".
- **Desktop popup:** search field on top, the clip list, a footer hint "Enter to paste  Esc to close".
- **Desktop toast:** small card, platform icon tile, "From {person} · {device}" (just "From {device}" when no person is shown) in 600, a one-line preview ("Image W×H" for images); hides after 4 s and never takes focus.
- **Android top bar:** 32dp logo tile, the two-tone wordmark, the connection chip and a settings button.

## Copy

Plain, sentence case, says what happens. Buttons name the action ("Copy", "Delete", "Sign in"), the result echoes it ("Copied"). Errors say what went wrong and what to do ("Sign in to PrivatePaste first"). No exclamation marks, no apologies. Time is relative and short ("just now", "12 min ago", "yesterday").

## Where it lives

| Platform | Tokens | Icons | Font |
|---|---|---|---|
| Web | `src/PrivatePaste.Server/wwwroot/site.css` (`:root` custom properties, light under `prefers-color-scheme`) | `src/PrivatePaste.Server/Pages/PlatformIcons.cs` (inline SVG) | `wwwroot/fonts/` |
| Desktop | `src/PrivatePaste.Desktop/Theme/NightVault.axaml` (`Pp*Color` / `Pp*` brushes, per theme variant) | `NightVault.axaml` StreamGeometry resources, `Theme/PlatformIcons.cs` | bundled in `Assets/` |
| Android | `android/app/src/main/java/xyz/ficdev/privatepaste/ui/theme/Color.kt`, `Theme.kt`, `Type.kt` | `res/drawable/ic_platform_*.xml`, `ic_kind_*.xml`, `ui/history/PlatformIcon.kt` | `res/font/` |
| README | `scripts/readme/hero.html` | — | outlined lockups |
| Windows installer | `installer/PrivatePasteUI.wxs` (light text styles over the dark artwork) and `installer/art/art.html`, rendered to `dialog.bmp` / `banner.bmp` by `scripts/make-installer-art.ps1` | logo tile in the artwork | Tahoma (MSI can't load Sora); wordmark outlined in the artwork |

## Checking a UI change

1. Render both themes: desktop `DesktopRenderTests` and Android `ScreenshotTest` with `PP_SCREENSHOT_DIR` set; the web UI against a local dev server in a browser, dark and light.
2. Check a phone-width web layout too (the People page stacks on narrow screens).
3. If the web history, desktop popup or Android history changed visibly, rebuild the README hero with `scripts/readme/build.ps1`.
