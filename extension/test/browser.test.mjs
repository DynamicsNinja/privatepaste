import { test } from 'node:test';
import assert from 'node:assert/strict';
import { browserName } from '../src/core/browser.js';

const nav = (brands, userAgent = '') => ({ userAgentData: brands && { brands: brands.map(brand => ({ brand, version: '1' })) }, userAgent });

test('names the browser from its brands', () => {
  assert.equal(browserName(nav(['Chromium', 'Microsoft Edge', 'Not.A/Brand'])), 'Edge');
  assert.equal(browserName(nav(['Chromium', 'Google Chrome', 'Not.A/Brand'])), 'Chrome');
  assert.equal(browserName(nav(['Chromium', 'Opera', 'Not.A/Brand'])), 'Opera');
  assert.equal(browserName(nav(['Chromium', 'Brave', 'Not.A/Brand'])), 'Brave');
});

test('falls back to the user agent string without brands', () => {
  assert.equal(browserName(nav(null, 'Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/150.0 Safari/537.36 Edg/150.0')), 'Edge');
  assert.equal(browserName(nav(null, 'Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/150.0 Safari/537.36 OPR/120.0')), 'Opera');
  assert.equal(browserName(nav(null, 'Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/150.0 Safari/537.36')), 'Chrome');
});

test('an unknown browser gets a neutral name', () => {
  assert.equal(browserName(nav(['Chromium', 'Not.A/Brand'])), 'Browser');
  assert.equal(browserName(nav(null, 'Node.js/22')), 'Browser');
  assert.equal(browserName(undefined), 'Browser');
});
