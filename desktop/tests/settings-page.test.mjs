import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const html = (await readFile(new URL('../ui/settings.html', import.meta.url), 'utf8')).replace(/\r\n/g, '\n');
const css = await readFile(new URL('../ui/settings.css', import.meta.url), 'utf8');
const visibleText = markup => markup.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');

test('settings copy only promises what the app does', () => {
  const text = visibleText(html);
  // TRADE and TEAMFIGHT are not eligible events, and there is no Timeline Inbox.
  for (const stale of [/timeline inbox/i, /\btrades?\b/i, /teamfights?/i, /Tilt Fix/, /Recording can continue/, /—/]) {
    assert.doesNotMatch(text, stale);
  }
  assert.doesNotMatch(html, /data-search="[^"]*\binbox\b/);
  assert.match(text, /Mark learning objective moments after each game/);
  assert.match(text, /Revu minimizes once your game loads/);
});

test('the bundled recorder exposes beta controls only after host availability is known', () => {
  const ascent = html.indexOf('id="ascent-settings"'), recorder = html.indexOf('id="recording-settings"');
  assert.ok(ascent > 0 && recorder > ascent, 'existing folder import remains available');
  const card = html.slice(html.lastIndexOf('<div', recorder), html.indexOf('<p class="settings-inline-note"', recorder));
  const controls = card.indexOf('<div class="settings-recording-controls" id="recording-controls" hidden>');
  assert.ok(controls > 0, 'recorder controls stay hidden until host availability is known');
  for (const id of ['recordingEnabled', 'recordingPreset', 'recordingPriority', 'save-recording-settings', 'open-recordings-folder']) {
    assert.ok(card.indexOf(`id="${id}"`) > controls, id);
  }
  assert.equal(visibleText(card.slice(0, controls)).trim(),
    'Beta Checking built-in recording availability…');
  assert.doesNotMatch(visibleText(html.slice(ascent, recorder)), /built-in recording/i);
  assert.match(card, /id="recording-encoder"/);
  assert.doesNotMatch(visibleText(card), /requires an NVIDIA GPU/);
  assert.doesNotMatch(card, /<select[^>]+id="[^"]*encoder/i);
});

test('Reset all data keeps the destructive treatment instead of a neutral button', () => {
  const rules = [...css.matchAll(/([^{}]*\.set-danger-btn[^{}]*)\{([^}]*)\}/g)];
  assert.ok(rules.length > 0);
  for (const [, selector, body] of rules) {
    assert.doesNotMatch(body, /color:var\(--text\)/, selector);
    if (!selector.includes(':disabled')) assert.match(body, /(^|;)color:var\(--danger\)/, selector);
  }
  assert.match(css, /\.set-danger>summary\{color:var\(--danger\)/);
});
