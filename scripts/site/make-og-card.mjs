// Screenshots the homepage's social card (og-card.html, #card at 1200x630) as a PNG. Run by
// scripts/make-site-images.py, which compresses it into site/images/; see that script for when and how.
//
//   node scripts/site/make-og-card.mjs <output png>
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
// Playwright is installed once for the README images (scripts/readme); reuse it instead of a second copy.
const { chromium } = createRequire(join(here, '..', 'readme', 'package.json'))('playwright');
const out = process.argv[2];
if (!out) throw new Error('usage: node make-og-card.mjs <output png>');

// Installed Edge first, so no browser download is needed; Playwright's own Chromium otherwise.
const browser = await chromium.launch({ channel: 'msedge' }).catch(() => chromium.launch());
try {
  const page = await (await browser.newContext({ viewport: { width: 1300, height: 700 }, deviceScaleFactor: 1 })).newPage();
  await page.goto(pathToFileURL(join(here, 'og-card.html')).href);
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
  const card = page.locator('#card');
  const box = await card.boundingBox();
  if (Math.round(box.width) !== 1200 || Math.round(box.height) !== 630) {
    throw new Error(`#card is ${box.width}x${box.height}, expected 1200x630`);
  }
  await card.screenshot({ path: out });
} finally {
  await browser.close();
}
