"""Writes branding/lockup-on-dark.svg and lockup-on-light.svg: logo tile + outlined 'privatepaste' wordmark.

Usage: pip install fonttools brotli; python scripts/make-lockup.py <repo root>
"""
import sys
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen

repo = sys.argv[1]
font_path = repo + "/src/PrivatePaste.Server/wwwroot/fonts/sora-latin.woff2"

SIZE = 38.0        # font size in SVG units (logo tile is 64)
BASELINE = 45.0
X0 = 82.0          # text starts after the 64-unit tile plus a gap


def run(word, weight, x):
    f = instantiateVariableFont(TTFont(font_path), {"wght": weight})
    upm = f["head"].unitsPerEm
    scale = SIZE / upm
    cmap, gs, hmtx = f.getBestCmap(), f.getGlyphSet(), f["hmtx"]
    pen = SVGPathPen(gs)
    for ch in word:
        name = cmap[ord(ch)]
        # font units are y-up; SVG is y-down
        gs[name].draw(TransformPen(pen, (scale, 0, 0, -scale, x, BASELINE)))
        x += hmtx[name][0] * scale
    return pen.getCommands(), x


private_d, x = run("private", 600, X0)
paste_d, width = run("paste", 300, x)
width = round(width + 2)

TILE = """  <rect x="2" y="2" width="60" height="60" rx="18" fill="#221D40"/>
  <rect x="2.75" y="2.75" width="58.5" height="58.5" rx="17.25" fill="none" stroke="#B7A5FF" stroke-opacity=".35" stroke-width="1.5"/>
  <circle cx="32" cy="26" r="9" fill="#B7A5FF"/>
  <path d="M27.5 31.5h9l3 17.5h-15z" fill="#B7A5FF"/>"""

for name, text, accent in [("on-dark", "#E9E6F7", "#B7A5FF"), ("on-light", "#1E1A38", "#5B45D6")]:
    svg = f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} 64" width="{width * 4}" height="256" role="img" aria-label="PrivatePaste">
{TILE}
  <path d="{private_d}" fill="{text}"/>
  <path d="{paste_d}" fill="{accent}"/>
</svg>
"""
    open(f"{repo}/branding/lockup-{name}.svg", "w", encoding="utf-8").write(svg)
print("width", width)
