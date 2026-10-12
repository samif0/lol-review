import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Copy rules for the pages below: no em or en dashes in what players read, sentence
// case, one honest claim per feature. Comments and console messages are not copy.
const ui = new URL('../ui/', import.meta.url);
const read = name => readFileSync(new URL(name, ui), 'utf8');
const visibleText = html => html.replace(/<!--[\s\S]*?-->/g, '').replace(/<script[\s\S]*?<\/script>/g, '')
  .replace(/<style[\s\S]*?<\/style>/g, '').replace(/<svg[\s\S]*?<\/svg>/g, '');

test('tilt check states one duration and makes no physiological claim', () => {
  const html = visibleText(read('tiltcheck.html'));
  const js = read('tiltcheck.js');
  assert.doesNotMatch(html + js, /cortisol|arousal|90-second|4-minute|~4/i);
  assert.match(html, /about 3 minutes, including 80 seconds of breathing/);
  assert.match(html, /id="tc-runbtn"[^>]*>Start a reset</);
  assert.match(js, /TOTAL_BREATH_SECONDS = 80;/, 'the stated 80 seconds is the real breathing timer');
});
