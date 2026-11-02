import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { classifyText } from '../src/core/classify.js';

const vectors = JSON.parse(readFileSync(new URL('../../docs/classifier-test-vectors.json', import.meta.url), 'utf8'));

for (const c of vectors.cases) {
  test(`classifies ${JSON.stringify(c.text)} as ${c.link ? 'link' : 'text'}`, () => {
    assert.equal(classifyText(c.text), c.link ? 'link' : 'text');
  });
}

test('NEL inside is whitespace to .NET', () => {
  assert.equal(classifyText('https://example.com/a\u0085b'), 'text');
});
