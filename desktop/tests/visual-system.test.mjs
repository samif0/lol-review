import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';

// Guards for the shared visual system: one system font, an 11px type floor, and
// error text that never falls back to the grey helper-text tokens.
const ui = new URL('../ui/', import.meta.url);
const read = name => readFileSync(new URL(name, ui), 'utf8');
const withoutComments = css => css.replace(/\/\*[\s\S]*?\*\//g, '');
const SHARED_CSS = ['styles.css', 'learning-pages.css', 'objective-progress.css', 'matchups-page.css',
  'patterns-page.css', 'tutorial.css'];

/** Every `selector { body }` pair, with @media/@supports bodies flattened in. */
function rules(css) {
  const out = [];
  const source = withoutComments(css);
  const walk = (text) => {
    let at = 0;
    while (at < text.length) {
      const open = text.indexOf('{', at);
      if (open < 0) return;
      let depth = 1, close = open + 1;
      while (close < text.length && depth) {
        if (text[close] === '{') depth++;
        else if (text[close] === '}') depth--;
        close++;
      }
      const selector = text.slice(at, open).trim();
      const body = text.slice(open + 1, close - 1);
      if (/^@(media|supports)/.test(selector)) walk(body);
      else if (!selector.startsWith('@')) out.push({ selector, body });
      at = close;
    }
  };
  walk(source);
  return out;
}

test('the app ships no webfonts and renders the Windows UI face', () => {
  const css = read('styles.css');
  assert.doesNotMatch(css, /@font-face/);
  assert.equal(existsSync(new URL('fonts/', ui)), false, 'desktop/ui/fonts must not come back');
  assert.match(css, /--font-ui:'Segoe UI'/);
});

test('shared and page stylesheets never render text below 11px', () => {
  for (const file of SHARED_CSS) {
    for (const { selector, body } of rules(read(file))) {
      for (const [, size] of body.matchAll(/font-size:\s*([\d.]+)px/g)) {
        assert.ok(Number(size) >= 11, `${file} ${selector} uses ${size}px`);
      }
      for (const [, size] of body.matchAll(/font:[^;]*?([\d.]+)px/g)) {
        assert.ok(Number(size) >= 11, `${file} ${selector} uses ${size}px in a font shorthand`);
      }
    }
  }
  assert.match(read('styles.css'), /--font-label:11px/);
});

test('error messages use the error token, never the grey loss or muted tokens', () => {
  assert.match(read('styles.css'), /--error:#ffaaaa/);
  let checked = 0;
  for (const file of SHARED_CSS) {
    for (const { selector, body } of rules(read(file))) {
      if (!/(^|[\s.,:(-])(err|error)\b|-err\b|-error\b|is-error|errpanel \.vline/.test(selector) || /is-preview/.test(selector)) continue;
      const color = body.match(/(?:^|[;{\s])color:\s*([^;!}]+)/);
      if (!color) continue;
      checked++;
      assert.equal(color[1].trim(), 'var(--error)', `${file} ${selector}`);
    }
  }
  assert.ok(checked >= 10, `expected the app's error rules to be checked (saw ${checked})`);
});

test('removed pages and animations leave no CSS behind', () => {
  const css = withoutComments(read('styles.css'));
  // The shared layer forces animation:none; only the loading spinner is exempt.
  assert.deepEqual([...css.matchAll(/@keyframes ([\w-]+)/g)].map(m => m[1]), ['onb-spin']);
  assert.doesNotMatch(css, /data-sidebar-anim|\.(an|ses|srow|datenav)-/, 'Analytics, Session log and sidebar animation');
});

test('match results share one quiet green and red pair', () => {
  const css = read('styles.css');
  assert.match(css, /--result-win:#9ed7b9;--result-loss:#e6a5a8/);
  assert.match(css, /\[data-result="win"\]\{color:var\(--result-win\)\}/);
  assert.match(css, /\[data-result="loss"\]\{color:var\(--result-loss\)\}/);
});
