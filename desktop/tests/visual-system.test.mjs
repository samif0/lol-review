import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

// Guards for the shared visual system: tokens from one stylesheet (appearance.css),
// an 11px type floor, error text that never falls back to grey helper tokens, and
// no raw color literals outside the token file.
const ui = new URL('../ui/', import.meta.url);
const read = name => readFileSync(new URL(name, ui), 'utf8');
const withoutComments = css => css.replace(/\/\*[\s\S]*?\*\//g, '');
const SHARED_CSS = ['styles.css', 'kit.css', 'shell.css', 'appearance.css', 'learning-pages.css', 'objective-progress.css',
  'matchups-page.css', 'patterns-page.css', 'patterns-player.css', 'tutorial.css', 'dashboard.css', 'games-page.css',
  'settings.css', 'review-launchpad.css', 'vod-review.css', 'vod-cinema.css', 'vod-composer.css', 'vod-narration.css',
  'rules.css', 'tiltcheck.css', 'pregame.css', 'manualentry.css'];
const ALL_CSS = readdirSync(ui).filter(name => name.endsWith('.css')).sort();

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
const declared = body => [...body.matchAll(/(--[\w-]+)\s*:/g)].map(m => m[1]);

// Raw colors: hex, or rgb()/rgba()/hsl() with a literal first channel. rgba(var(--x-rgb),a) is a token tint.
const COLOR_LITERAL = /#(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{3,4})\b|\b(?:rgba?|hsla?)\(\s*[\d.]/i;
const COLOR_PROPERTY = /^(?:color|background(?:-[\w-]+)?|border(?:-[\w-]+)?|outline(?:-[\w-]+)?|fill|stroke|caret-color|accent-color|scrollbar-color|--[\w-]+)$/i;

/** Declarations with a raw color, unless a line of the declaration says literal-ok. */
function colorLiterals(css) {
  const allowed = new Set();
  css.split('\n').forEach((line, index) => { if (/literal-ok:/.test(line)) allowed.add(index); });
  // Blank comments but keep every line break, so offsets still map to lines.
  const text = css.replace(/\/\*[\s\S]*?\*\//g, comment => comment.replace(/[^\n]/g, ' '));
  const starts = [0];
  for (let i = 0; i < text.length; i++) if (text[i] === '\n') starts.push(i + 1);
  const lineOf = offset => { let lo = 0, hi = starts.length - 1; while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (starts[mid] <= offset) lo = mid; else hi = mid - 1; } return lo; };
  const found = [];
  for (const match of text.matchAll(/(^|[{;])\s*([-\w]+)\s*:([^;{}]*)/g)) {
    const [whole, lead, property, value] = match;
    if (!COLOR_PROPERTY.test(property) || !COLOR_LITERAL.test(value)) continue;
    const first = lineOf(match.index + lead.length), last = lineOf(match.index + whole.length);
    let ok = false;
    for (let line = first; line <= last; line++) if (allowed.has(line)) ok = true;
    if (!ok) found.push(`${first + 1}: ${property}:${value.trim().slice(0, 70)}`);
  }
  return found;
}

const CALM = {
  surfaces: ['--bg', '--side', '--raised', '--hover', '--selected', '--well', '--line', '--line-strong', '--field', '--field-line'],
  text: ['--t1', '--t2', '--t3'],
  accent: ['--accent', '--accent-ink', '--accent-soft', '--focus', '--accent-mark'],
  meaning: ['--win', '--loss', '--pending', '--pending-soft', '--ok', '--danger', '--gold', '--calm'],
  events: ['--ev-kill', '--ev-assist', '--ev-death', '--ev-dragon', '--ev-void', '--ev-structure', '--ev-fight', '--ev-recall',
    '--mk-clip', '--mk-note', '--playhead', '--track'],
  type: ['--f-display', '--f-body', '--f-data', '--display-weight', '--display-track', '--greet-size', '--title-size'],
  shape: ['--r-lg', '--r-md', '--r-sm', '--btn-radius', '--raised-shadow', '--raised-border'],
  tints: ['--primary-rgb', '--neutral-rgb', '--bg-rgb'],
};
const ALL_TOKENS = Object.values(CALM).flat();
const COLOR_TOKENS = [...CALM.surfaces, ...CALM.text, ...CALM.accent, ...CALM.meaning, ...CALM.events,
  '--raised-shadow', '--raised-border', ...CALM.tints];
const AURA_TOKENS = ['--aura-core', '--aura-wide', '--aura-y', '--aura-rx', '--aura-ry', '--lit', '--lit-soft', '--lit-far',
  '--lit-edge', '--glow', '--composer-bg', '--composer-sky', '--composer-edge', '--composer-shadow'];
const BLOCKS = {
  dark: { warm: ':root:not([data-theme]),[data-theme="warm"],[data-theme="journal"]', neutral: '[data-theme="neutral"]', aura: '[data-theme="aura"]' },
  light: { warm: '[data-theme="warm"][data-mode="light"],[data-theme="journal"][data-mode="light"]',
    neutral: '[data-theme="neutral"][data-mode="light"]', aura: '[data-theme="aura"][data-mode="light"]' },
};
const appearance = rules(read('appearance.css'));
const block = selector => {
  const found = appearance.filter(rule => rule.selector.replace(/\s+/g, '') === selector);
  assert.equal(found.length, 1, `exactly one ${selector} block`);
  return declared(found[0].body);
};

test('every stylesheet is covered by the shared visual guards', () => {
  assert.deepEqual([...SHARED_CSS].sort(), ALL_CSS, 'add a new stylesheet to SHARED_CSS');
});

test('appearance.css owns the faces, the color scheme and every import-free token', () => {
  for (const file of ALL_CSS) {
    const css = withoutComments(read(file));
    assert.doesNotMatch(css, /@import/, `${file} imports`);
    if (file !== 'appearance.css') {
      assert.doesNotMatch(css, /@font-face/, `${file} declares a face`);
      assert.doesNotMatch(css, /color-scheme:\s*dark/, `${file} pins the dark color scheme`);
    }
  }
  const css = withoutComments(read('appearance.css'));
  assert.match(css, /:root\{color-scheme:dark;font-synthesis-weight:none\}/);
  assert.match(css, /:root\[data-mode="light"\]\{color-scheme:light\}/);
  assert.ok(read('appearance.css').split('\n').length < 500, 'appearance.css stays under 500 lines');
  for (const { selector, body } of rules(read('styles.css')).filter(rule => rule.selector === ':root')) {
    assert.doesNotMatch(body, COLOR_LITERAL, `styles.css ${selector} holds no color values`);
  }
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
  assert.match(withoutComments(read('appearance.css')), /--error:var\(--danger\)/);
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
  // The shared layer forces animation:none; only the loading spinner is exempt.
  const frames = ALL_CSS.flatMap(file => [...withoutComments(read(file)).matchAll(/@keyframes ([\w-]+)/g)].map(m => `${file}:${m[1]}`));
  assert.deepEqual(frames, ['styles.css:onb-spin']);
  assert.doesNotMatch(withoutComments(read('styles.css')), /data-sidebar-anim|\.(an|ses|srow|datenav)-/, 'Analytics, Session log and sidebar animation');
});

test('match results read the theme result tokens', () => {
  assert.match(withoutComments(read('appearance.css')), /--result-win:var\(--win\);--result-loss:var\(--loss\)/);
  const css = read('styles.css');
  assert.match(css, /\[data-result="win"\]\{color:var\(--result-win\)\}/);
  assert.match(css, /\[data-result="loss"\]\{color:var\(--result-loss\)\}/);
  assert.match(css, /\[data-result="remake"\]\{color:var\(--t3\)\}/);
});

test('no stylesheet but appearance.css holds a raw color unless the line says literal-ok', () => {
  const failures = ALL_CSS.filter(file => file !== 'appearance.css')
    .flatMap(file => colorLiterals(read(file)).map(found => `${file}:${found}`));
  assert.deepEqual(failures, []);
  // The guard itself: literals are caught in every color property, tints and literal-ok lines pass.
  assert.deepEqual(colorLiterals('.a{color:#fff}\n.b{background:linear-gradient(#000,rgba(0,0,0,.4))}\n.c{--x:rgb(1 2 3)}'),
    ['1: color:#fff', '2: background:linear-gradient(#000,rgba(0,0,0,.4))', '3: --x:rgb(1 2 3)']);
  assert.deepEqual(colorLiterals('#id-abc .a{border-color:rgba(var(--primary-rgb),.1);fill:var(--t1)}\n'
    + '.b{color:#fff} /* literal-ok: over video */\n.c{box-shadow:0 0 0 1px #000;width:calc(100% - 3px)}'), []);
});

test('each theme defines the whole token contract in dark and every color in light', () => {
  for (const [theme, selector] of Object.entries(BLOCKS.dark)) {
    const names = new Set(block(selector));
    for (const token of ALL_TOKENS) assert.ok(names.has(token), `${theme} dark defines ${token}`);
  }
  for (const [theme, selector] of Object.entries(BLOCKS.light)) {
    const names = new Set(block(selector));
    for (const token of COLOR_TOKENS) assert.ok(names.has(token), `${theme} light defines ${token}`);
  }
  for (const selector of [BLOCKS.dark.aura, BLOCKS.light.aura]) {
    const names = new Set(block(selector));
    for (const token of [...AURA_TOKENS, '--lit-fill', '--lit-shadow', '--composer-border']) assert.ok(names.has(token), `${selector} ${token}`);
  }
  assert.deepEqual(block('[data-theme="journal"]').sort(), ['--f-voice', '--spine', '--voice']);
  assert.deepEqual(block('[data-theme="journal"][data-mode="light"]').sort(), ['--spine', '--voice']);
  // The bridge comes first so a theme can override its mixins; it never names a raw color.
  const bridgeAt = appearance.findIndex(rule => rule.selector.replace(/\s+/g, '') === ':root,[data-theme]');
  const firstTheme = appearance.findIndex(rule => rule.selector.replace(/\s+/g, '') === BLOCKS.dark.warm);
  assert.ok(bridgeAt >= 0 && bridgeAt < firstTheme, 'the bridge precedes the theme blocks');
  assert.doesNotMatch(appearance[bridgeAt].body, COLOR_LITERAL);
  for (const legacy of ['--text', '--muted', '--surface', '--surface-raised', '--border', '--primary', '--primary-ink', '--error',
    '--result-win', '--result-loss', '--font-ui', '--fo', '--fb', '--fm', '--f-num', '--ev-summoner', '--ev-trade', '--ev-away',
    '--ev-proximity', '--selection', '--scroll-thumb', '--accent-hover', '--danger-line', '--danger-soft', '--scrim', '--pop-shadow',
    '--lit-fill', '--lit-shadow', '--glow', '--composer-bg', '--composer-sky', '--composer-border', '--composer-shadow']) {
    assert.ok(declared(appearance[bridgeAt].body).includes(legacy), `bridge defines ${legacy}`);
  }
  assert.match(appearance[bridgeAt].body, /--fm:var\(--f-body\)/, '--fm labels stay in the body face');
  // Aura's time of day moves only the light field; the VOD page turns the lights off in both modes.
  const dayparts = appearance.filter(rule => /\[data-daypart=/.test(rule.selector));
  assert.equal(dayparts.length, 6);
  for (const rule of dayparts) assert.deepEqual(declared(rule.body).sort(), ['--aura-core', '--aura-wide', '--aura-y'], rule.selector);
  for (const mode of ['dark', 'light']) {
    const names = block(`[data-theme="aura"][data-mode="${mode}"]:is([data-page="vodplayer"],[data-frame-page="vodplayer"])`);
    for (const token of ['--bg', '--side', '--raised', '--t1', '--t2', '--t3', '--accent', '--accent-ink', '--primary-rgb', '--bg-rgb']) {
      assert.ok(names.includes(token), `Aura ${mode} VOD sets ${token}`);
    }
  }
});

test('theme-only surfaces are gated by attribute for all four themes, never by [hidden]', () => {
  const css = withoutComments(read('appearance.css'));
  for (const theme of ['warm', 'neutral', 'journal', 'aura']) {
    assert.match(css, new RegExp(`:root\\[data-theme="${theme}"\\] \\[data-theme-only\\]:not\\(\\[data-theme-only~="${theme}"\\]\\)`), theme);
    assert.match(css, new RegExp(`:root\\[data-theme="${theme}"\\] \\[data-theme-hide~="${theme}"\\]`), theme);
  }
  assert.match(css, /\[data-theme-hide~="aura"\]\{display:none!important\}/);
  assert.match(css, /:root\[data-theme="journal"\] \.own-words\{font-family:var\(--f-voice\);color:var\(--voice\)/);
});

test('focus stays visible under forced colors: fields draw a transparent outline, never outline:none', () => {
  const kit = rules(read('kit.css'));
  const field = kit.find(rule => rule.selector === '.ui-field:focus-visible');
  assert.ok(field, 'kit.css owns the kit field outline');
  assert.match(field.body, /outline:2px solid transparent/);
  for (const [file, selector] of [['dashboard.css', '.home-composer .home-composer-input:focus-visible'],
    ['vod-review.css', '.vod-review-page .vp-review-note .vp-note-input:focus-visible']]) {
    const rule = rules(read(file)).find(r => r.selector === selector);
    assert.ok(rule, `${file} ${selector}`);
    assert.match(rule.body, /outline:2px solid transparent!important/, `${file} ${selector}`);
    assert.doesNotMatch(rule.body, /outline:none/, `${file} ${selector}`);
  }
});

test('the note card in the save dock draws no ring of its own while its field is focused', () => {
  // vod-review.css rings the review-column card through :has(#vp-bm-note:focus) with
  // !important; in the dock the field has its own ring, so the flattened card must
  // cancel the halo with a selector that outranks the :has() id.
  const ring = rules(read('vod-review.css'))
    .find(r => r.selector === '.vod-review-page .vp-review-note.ui-raised:has(#vp-bm-note:focus)');
  assert.ok(ring, 'review-column card ring');
  const dock = rules(read('vod-composer.css'))
    .find(r => r.selector === '.vod-review-page .vp-composer #vp-bookmark-tools:has(#vp-bm-note:focus)');
  assert.ok(dock, 'vod-composer.css cancels the card ring in the dock');
  assert.match(dock.body, /box-shadow:\s*none\s*!important/);
  assert.match(dock.body, /border-color:\s*transparent/);
});

const channels = color => [1, 3, 5].map(at => parseInt(color.slice(at, at + 2), 16));
const luminance = color => {
  const [r, g, b] = channels(color).map(c => { const v = c / 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};
const contrast = (a, b) => { const x = luminance(a), y = luminance(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
const mixHex = (a, b, weight) => '#' + channels(a).map((c, i) => Math.round(c * weight + channels(b)[i] * (1 - weight))
  .toString(16).padStart(2, '0')).join('');

test('small text in light modes reaches 4.5:1: clip and calm text tokens, timeline codes and muted counts', () => {
  const token = (body, name) => new RegExp(`${name}:([^;]+);`).exec(body)?.[1].trim();
  const body = selector => appearance.find(rule => rule.selector.replace(/\s+/g, '') === selector).body;
  const bridge = body(':root,[data-theme]');
  assert.equal(token(bridge, '--mk-clip-text'), 'var(--mk-clip)');
  assert.equal(token(bridge, '--calm-text'), 'var(--calm)');
  assert.equal(token(bridge, '--ev-code-mix'), '72%');
  const vodLight = body('[data-theme="aura"][data-mode="light"]:is([data-page="vodplayer"],[data-frame-page="vodplayer"])');
  for (const [theme, selector] of Object.entries(BLOCKS.light)) {
    const block = body(selector);
    const surfaces = ['--bg', '--side', '--raised'].map(name => token(block, name));
    if (theme === 'aura') surfaces.push(token(vodLight, '--bg'), token(vodLight, '--side'));
    for (const name of ['--mk-clip-text', '--calm-text']) {
      for (const surface of surfaces) assert.ok(contrast(token(block, name), surface) >= 4.5, `${theme} light ${name} on ${surface}`);
    }
    const mix = parseFloat(token(block, '--ev-code-mix')) / 100;
    assert.equal(mix, 0.5, `${theme} light --ev-code-mix`);
    const ink = [[token(block, '--t1'), token(block, '--bg')]];
    if (theme === 'aura') ink.push([token(vodLight, '--t1'), token(vodLight, '--bg')]);
    for (const event of ['--ev-recall', '--ev-structure', '--mk-clip', '--mk-note', '--ev-dragon', '--ev-void']) {
      for (const [t1, bg] of ink) {
        assert.ok(contrast(mixHex(token(block, event), t1, mix), bg) >= 4.5, `${theme} light ${event} timeline code on ${bg}`);
      }
    }
  }
  const styles = rules(read('styles.css'));
  for (const selector of ['.seclabel .n', '.rv-clipgroup-lbl .n']) {
    const rule = styles.find(r => r.selector === selector);
    assert.ok(rule, selector);
    assert.doesNotMatch(rule.body, /opacity/, `${selector} keeps --t3 at full strength`);
  }
  for (const [selector, value] of [['.vp-fix-state.is-active', 'var(--mk-clip-text)'], ['.vp-cliprange', 'var(--mk-clip-text)'],
    ['.tc-row-after', 'var(--calm-text)']]) {
    assert.ok(styles.find(r => r.selector === selector).body.includes(`color:${value}`), selector);
  }
  const vod = rules(read('vod-review.css'));
  for (const selector of ['.vod-review-page .vp-timeline .evbar-code', '.vod-review-page .vp-timeline .evbar-nolabel::after']) {
    assert.match(vod.find(r => r.selector === selector).body, /color-mix\(in srgb,var\(--evc\) var\(--ev-code-mix\),var\(--t1\)\)/, selector);
  }
});
