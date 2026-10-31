"""Writes the homepage's images (site/) from the README hero, the README shots and the branding:

  site/images/hero-{dark,light}-{800,1200,2000}.webp  the hero, its empty top backdrop cropped away
  site/images/hero-{dark,light}.png                   1200 px PNG fallback for browsers without WebP
  site/images/slide-{android,desktop,web}-{dark,light}-{320,640}.webp
                                                      the phone carousel: one app per slide (scripts/site/carousel.html)
  site/images/og-card.png                             1200x630 social card (scripts/site/og-card.html)
  site/favicon.ico, site/apple-touch-icon.png         from branding/app-icon-512.png

The carousel slides are made from the separate shots in scripts/readme/out/ (git-ignored), so run this right after
`powershell -File scripts/readme/build.ps1` (which also remakes the hero), or after the logo or the card changes;
then look at the files and commit them:
  python scripts/make-site-images.py
Needs Pillow, Node.js and Playwright (`npm ci` in scripts/readme, as for the README).
"""
import os
import subprocess
import sys
import tempfile

from PIL import Image, ImageDraw

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SITE = os.path.join(REPO, "site")
IMAGES = os.path.join(SITE, "images")

# The hero is scripts/readme/hero.html (a 1600x1000 CSS px stage) rendered at 1.5x. Positions below are in its
# CSS pixels; keep them in step with hero.html.
SCALE = 1.5
STAGE_RADIUS = 28
HERO_TOP = 46          # the browser window starts at 70: keep a little of the backdrop above it
HERO_WIDTHS = (800, 1200, 2000)
# Slides are 320x480 CSS px (carousel.html) rendered at 2x; the page shows them at most 320 px wide.
SLIDE_WIDTHS = (320, 640)


def px(v):
    return round(v * SCALE)


def rounded(im, radius):
    """Makes the corners outside a rounded rectangle transparent, keeping the existing alpha inside."""
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, im.width - 1, im.height - 1), radius=radius, fill=255)
    out = im.copy()
    out.putalpha(Image.composite(im.getchannel("A"), mask, mask))
    return out


def resized(im, width):
    return im if im.width == width else im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)


def hero(theme):
    src = Image.open(os.path.join(REPO, "docs", "images", f"hero-{theme}.png")).convert("RGBA")
    if src.size != (px(1600), px(1000)):
        sys.exit(f"hero-{theme}.png is {src.size}, expected {px(1600)}x{px(1000)}: update the positions here")

    full = rounded(src.crop((0, px(HERO_TOP), src.width, src.height)), px(STAGE_RADIUS))
    for width in HERO_WIDTHS:
        resized(full, width).save(os.path.join(IMAGES, f"hero-{theme}-{width}.webp"), quality=82, method=6)
    resized(full, 1200).save(os.path.join(IMAGES, f"hero-{theme}.png"), optimize=True)
    return full.size


def carousel():
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["node", os.path.join(REPO, "scripts", "site", "make-carousel.mjs"), tmp], check=True)
        for name in sorted(os.listdir(tmp)):
            slide = Image.open(os.path.join(tmp, name)).convert("RGBA")
            if slide.size != (640, 960):
                sys.exit(f"{name} is {slide.size}, expected 640x960")
            for width in SLIDE_WIDTHS:
                resized(slide, width).save(os.path.join(IMAGES, f"{name[:-4]}-{width}.webp"), quality=80, method=6)


def icons():
    icon = Image.open(os.path.join(REPO, "branding", "app-icon-512.png")).convert("RGBA")
    icon.save(os.path.join(SITE, "favicon.ico"), sizes=[(16, 16), (32, 32)])
    # iOS fills transparent corners with black, so the touch icon sits on the Night vault background.
    touch = Image.new("RGBA", icon.size, "#15122B")
    touch.alpha_composite(icon)
    touch.convert("RGB").resize((180, 180), Image.LANCZOS).save(os.path.join(SITE, "apple-touch-icon.png"), optimize=True)


def og_card():
    with tempfile.TemporaryDirectory() as tmp:
        shot = os.path.join(tmp, "og.png")
        script = os.path.join(REPO, "scripts", "site", "make-og-card.mjs")
        subprocess.run(["node", script, shot], check=True)
        card = Image.open(shot).convert("RGB")
        if card.size != (1200, 630):
            sys.exit(f"social card is {card.size}, expected 1200x630")
        # A 256-color palette keeps the gradient smooth enough and the file near 100 KB.
        card.quantize(256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.FLOYDSTEINBERG).save(
            os.path.join(IMAGES, "og-card.png"), optimize=True)


def main():
    os.makedirs(IMAGES, exist_ok=True)
    for theme in ("dark", "light"):
        full = hero(theme)
    print(f"hero {full[0]}x{full[1]} (at full size)")
    carousel()
    icons()
    og_card()
    for folder in (IMAGES, SITE):
        for name in sorted(os.listdir(folder)):
            path = os.path.join(folder, name)
            if os.path.isfile(path) and name.endswith((".webp", ".png", ".ico")):
                with Image.open(path) as im:
                    print(f"{os.path.relpath(path, REPO):40} {im.width:>5}x{im.height:<5} {os.path.getsize(path) // 1024:>5} KB")


if __name__ == "__main__":
    main()
