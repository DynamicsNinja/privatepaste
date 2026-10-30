// Screenshots the two installer pictures in installer/art/art.html as PNGs. Run by make-installer-art.ps1, which
// turns them into the 24-bit BMPs the MSI needs; see that script for when and how.
//
//   node scripts/make-installer-art.mjs <output folder>
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
// Playwright is installed once for the README images (scripts/readme); reuse it instead of a second copy.
const { chromium } = createRequire(join(here, 'readme', 'package.json'))('playwright');
const out = process.argv[2];
if (!out) throw new Error('usage: node make-installer-art.mjs <output folder>');

// Installed Edge first, so no browser download is needed; Playwright's own Chromium otherwise.
const browser = await chromium.launch({ channel: 'msedge' }).catch(() => chromium.launch());
try {
  const page = await (await browser.newContext({ viewport: { width: 600, height: 500 }, deviceScaleFactor: 1 })).newPage();
  await page.goto(pathToFileURL(join(here, '..', 'installer', 'art', 'art.html')).href);
  // The wordmark is an <image> of the lockup SVG; wait until it has been drawn.
  await page.waitForLoadState('networkidle');
  for (const [id, width, height] of [['dialog', 493, 312], ['banner', 493, 58]]) {
    const element = page.locator(`#${id}`);
    const box = await element.boundingBox();
    if (Math.round(box.width) !== width || Math.round(box.height) !== height) {
      throw new Error(`#${id} is ${box.width}x${box.height}, expected ${width}x${height}`);
    }
    await element.screenshot({ path: join(out, `${id}.png`) });
  }
} finally {
  await browser.close();
}
