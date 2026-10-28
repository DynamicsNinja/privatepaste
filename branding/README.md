# PrivatePaste brand: Night vault

Your own vault for everything you copy. Deep violet night with a soft lilac glow.

## Files

| File | Use |
|---|---|
| `logo.svg` | Logo with rounded corners and a hairline lilac edge. Web header, favicon, sign-in page. |
| `app-icon.svg` | Full-bleed square for app icons (the OS applies its own corner mask). |
| `app-icon-512.png` | 512×512 raster of `app-icon.svg` for installers and the Android launcher. |
| `platform-icons/` | 24×24 stroked icons for a clip's source device platform: `windows`, `macos` (⌘), `linux` (>_), `android`, `web` (globe), and `unknown` ("Unknown device", a monitor) for anything else or no platform. |

## Wordmark

`private` in Sora semibold (600) in the text color, followed directly by `paste` in Sora light (300) in the accent color. Lowercase, no space.

## Color

Dark is the default.

| Token | Dark | Light | Role |
|---|---|---|---|
| night | `#15122B` | `#F6F4FD` | Page background |
| surface | `#1D1838` | `#FFFFFF` | Rows, panels |
| raised | `#262046` | `#EFEBFC` | Inputs, icon tiles, active nav |
| line | `#2E2756` | `#E1DBF7` | Borders |
| text | `#E9E6F7` | `#1E1A38` | Body text |
| muted | `#9A94BD` | `#625C88` | Secondary text |
| accent | `#B7A5FF` | `#5B45D6` | Links, primary button, newest-clip glow |
| danger | `#FF8FA3` | `#C2364A` | Destructive actions, on hover/focus only |
| live | `#7CE0C3` | `#7CE0C3` | Connected indicator (apps only) |

Logo colors are fixed in both modes: tile `#221D40`, keyhole `#B7A5FF`.

## Type

Sora (SIL Open Font License, `src/PrivatePaste.Server/wwwroot/fonts/OFL.txt`), self-hosted, with no third-party font service. Weights: 300 (wordmark accent), 400 (body), 600 (headings, buttons, wordmark).

## Shape

Rows and panels use a 14px radius, controls 10px, and nav and pills are fully rounded. The newest clip gets an accent border plus a soft 3px glow; nothing else glows.
