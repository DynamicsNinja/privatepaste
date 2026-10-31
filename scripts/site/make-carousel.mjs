// Screenshots the homepage's carousel slides (carousel.html, one per app and theme) as 640x960 PNGs with
// transparent corners. Run by scripts/make-site-images.py, which resizes them into site/images/; see that script.
//
//   node scripts/site/make-carousel.mjs <output folder>
import { createRequire } from 'node:module';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
// Playwright is installed once for the README images (scripts/readme); reuse it instead of a second copy.
const { chromium } = createRequire(join(here, '..', 'readme', 'package.json'))('playwright');
const out = process.argv[2];
if (!out) throw new Error('usage: node make-carousel.mjs <output folder>');

const shots = join(here, '..', 'readme', 'out');
for (const theme of ['dark', 'light']) {
  for (const name of [`web-${theme}.png`, `readme-popup-${theme}.png`, `readme-history-${theme}.png`]) {
    if (!existsSync(join(shots, name))) {
      throw new Error(`missing scripts/readme/out/${name}: run scripts/readme/build.ps1 first`);
    }
  }
}

// Installed Edge first, so no browser download is needed; Playwright's own Chromium otherwise.
const browser = await chromium.launch({ channel: 'msedge' }).catch(() => chromium.launch());
try {
  const page = await (await browser.newContext({ viewport: { width: 400, height: 560 }, deviceScaleFactor: 2 })).newPage();
  for (const app of ['android', 'desktop', 'web']) {
    for (const theme of ['dark', 'light']) {
      const url = new URL(pathToFileURL(join(here, 'carousel.html')).href);
      url.search = `?app=${app}&t=${theme}`;
      await page.goto(url.href);
      await page.waitForLoadState('networkidle');
      await page.evaluate(() => Promise.all([...document.images].map((i) => i.decode())));
      await page.locator('#stage').screenshot({ path: join(out, `slide-${app}-${theme}.png`), omitBackground: true });
    }
  }
} finally {
  await browser.close();
}
