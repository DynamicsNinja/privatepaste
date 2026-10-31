# README images

How the pictures in the project README are made, and how to remake them when the apps change.

## When to rebuild

| What changed | Rebuild |
|---|---|
| The web history page, the desktop history popup or the Android history screen looks different | The hero: `docs/images/hero-dark.png`, `hero-light.png` |
| The demo clips should say something else | Edit `demo.json`, then the hero |
| The hero changed (either row above), the logo, or `scripts/site/og-card.html` or `carousel.html` | The homepage images: `python scripts/make-site-images.py`, right after the hero build (see [The homepage images](#the-homepage-images)) |
| The logo, wordmark or its colors | The lockups: `branding/lockup-on-dark.svg`, `lockup-on-light.svg` |
| App features, platforms, settings or install steps | The text: see [Text to keep in step](#text-to-keep-in-step) |

## The hero

```
powershell -File scripts/readme/build.ps1
```

It needs the .NET SDK, Node.js and a JDK 21 (`JAVA_HOME`, or Android Studio's / Microsoft's JDK, found automatically). It takes a few minutes and does this, all locally:

1. Publishes the server into `scripts/readme/out/server` and starts it on http://localhost:5311 with a fresh, empty data directory; the owner is `owner` with the password `dev`. It never touches a real server.
2. Renders the desktop popup from `demo.json` with the real Avalonia theme: the test `DesktopRenderTests.Readme_popup` writes `out/readme-popup-{dark,light}.png` at 2x (840×960), so it stays sharp where it is shown larger.
3. Renders the Android history from `demo.json` with Robolectric: `ReadmeScreenshotTest` writes `out/readme-history-{dark,light}.png`. Image clips are left out there, because the test has no thumbnail to load.
4. Runs `capture.mjs` (Playwright, using the installed Edge or Playwright's Chromium):
   - renders `photo.html` to `out/photo.png`, the picture used for the image clip;
   - signs in to the web UI as `owner`, renames the owner to the first `user` in `demo.json` and adds the other people on the People page (one sign-in);
   - signs in once per demo device (Work laptop/Windows, Pixel 9/Android, MacBook Air/macOS, ThinkPad/Linux), each as its person, and uploads the clips oldest first as backfilled clips, so each keeps its age ("12 min ago", "yesterday");
   - saves `out/web-{dark,light}.png` from that web session (880×860 at 2×);
   - composes `hero.html` (browser frame with the web shot, the popup over its lower left, the phone on the right, on a Night vault background with rounded transparent corners) and saves `docs/images/hero-{dark,light}.png` at 1.5×.
5. Stops the server. `out/` is git-ignored and recreated on every run.

Then look at both PNGs and commit them. The README shows the dark one in dark mode and the light one otherwise, through `<picture>`.

### Changing it

- **Demo content:** `demo.json`, newest first. `user` is the person who copied it (the first one named is the owner; each device belongs to one person, and the name chips only show because there are two people). `kind` is `text`, `link` or `image`; `platform` picks the icon. Keep it to 4 distinct devices: the server allows 5 sign-ins a minute and the web UI needs one. Keep it realistic and free of personal data.
- **Layout:** `hero.html`. Positions are in CSS pixels on a 1600×1000 stage; the colors at the top are the `DESIGN.md` tokens.
- **Picture in the image clip:** `photo.html` (an inline SVG).
- **Sizes:** the web viewport and scale are in `capture.mjs`; the phone size comes from the Robolectric qualifiers in `ReadmeScreenshotTest`; the popup is its real window size (420×480), drawn at 2x.

### If a step fails

- *Server did not answer*: read `out/server.err.log`. Port 5311 may be taken: `build.ps1 -Port 5312`.
- *HTTP 429 on sign-in*: too many sign-ins within a minute (more than 4 devices in `demo.json`, or a rerun straight after a run). Wait a minute and rerun.
- *No JDK found*: set `JAVA_HOME` to a JDK 21.
- *Browser launch fails*: without Edge, run `npx playwright install chromium` in this folder once.

## The homepage images

```
python scripts/make-site-images.py
```

The homepage (`site/`) doesn't serve the 2400 px hero PNGs: this script writes web-sized copies to `site/images/` (committed) and the site Dockerfile copies that folder. It needs Pillow, plus Node.js and Playwright from this folder for the carousel and the social card.

Run it right after `build.ps1`: the carousel is made from the separate shots in `out/` (git-ignored), not from the hero, and the script stops if they are missing.

- `hero-{dark,light}-{800,1200,2000}.webp` and a 1200 px PNG fallback: the hero with its empty top backdrop cropped away, for screens wider than 640 px.
- `slide-{android,desktop,web}-{dark,light}-{320,640}.webp`: the phone carousel, one app per slide, framed like the hero (phone, floating popup, browser window) on a transparent 320×480 stage: `scripts/site/carousel.html`, rendered at 2x by `scripts/site/make-carousel.mjs`. The web slide shows the top of the history page; its crop is in `carousel.html`.
- `og-card.png`: the 1200×630 social card, a screenshot of `scripts/site/og-card.html`.
- `site/favicon.ico` and `site/apple-touch-icon.png`, from `branding/app-icon-512.png`.

The hero crop is in CSS pixels of `hero.html`'s stage; if you change the stage size there, update it at the top of the script. Look at the images, then commit them with the hero.

## The lockups

```
pip install fonttools brotli
python scripts/make-lockup.py .
```

Draws the logo tile plus the `privatepaste` wordmark with the outlines of the bundled Sora font (600 for "private", 300 for "paste"), so the SVGs need no font. Colors are in the script and follow `DESIGN.md`.

## Text to keep in step

The README states these facts; check them when the apps change:

- The feature list under "Why PrivatePaste": multiple people with one shared history, the hotkey (Ctrl+Shift+V), the Quick Settings tile and Share target, the clip-arrival notification, "kept until you delete it".
- The Apps table: release file names (`.msi`, `.zip`, `osx-arm64`/`osx-x64`, `linux-x64.tar.gz`, `.apk`), Android 10 minimum, signing/notarization status.
- Quick start: the environment variables (`PP_PASSWORD` is the owner's password, the owner signs in as `owner`), the upgrade note for servers several people already share, port 8080, service name `privatepaste`, the prebuilt image name.
- Security: Argon2id, the sign-in rate limit, what removing a person or resetting a password signs out, owner-only actions, HTTPS-only clients, and that this is encryption at rest, not end-to-end.
- Badges: the `desktop-v*` and `android-v*` tag filters. Release badges need the repository to be public.
