// The site's look is the desktop app's Neutral dark theme: tokens.css holds the
// only color literals and copies desktop/ui/appearance.css value for value, the
// fonts are byte-identical copies of the app's, and every page loads them the
// same way.
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';

const site = new URL('../', import.meta.url);
const appUi = new URL('../../desktop/ui/', import.meta.url);
const read = (name, base = site) => readFileSync(new URL(name, base), 'utf8').replace(/\r\n/g, '\n');
const stripComments = css => css.replace(/\/\*[\s\S]*?\*\//g, '');
const PAGE_CSS = ['styles.css', 'landing.css', 'webapp.css', 'account.css', 'clip.css'];
const PAGES = ['index.html', 'review.html', 'privacy.html', 'terms.html', 'discord.html', 'discord/index.html',
  'sign-in.html', 'my-clips.html', 'account.html', 'clip.html'];

const declarations = block => Object.fromEntries([...block.matchAll(/(--[\w-]+)\s*:\s*([^;}]+)/g)].map(m => [m[1], m[2].trim()]));

// A hex color, a color function or a named color keyword. Only declaration values are
// read (the text after a property's colon inside the innermost {...} blocks), so ID
// selectors such as #download never count and a gradient stop or a second shadow does.
const COLOR_LITERAL = new RegExp(['#[0-9a-f]{3,8}\\b', '\\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch)\\(',
  '(?<![\\w-])(?:white|black|red|green|blue|gray|grey|silver|yellow|orange|purple|pink|navy|teal|maroon|olive|lime|aqua|fuchsia|cyan|magenta)(?![\\w-])'].join('|'), 'i');
const colorLiterals = css => [...stripComments(css).matchAll(/\{([^{}]*)\}/g)]
  .flatMap(block => block[1].split(';'))
  .filter(declaration => declaration.includes(':'))
  .map(declaration => declaration.slice(declaration.indexOf(':') + 1).trim())
  .filter(value => COLOR_LITERAL.test(value));

test('The color literal check reads every declaration value and no selector', () => {
  for (const css of ['.a{background:linear-gradient(#123456, var(--bg))}', '.a{box-shadow:0 0 0 1px #000, 0 1px 2px var(--x)}',
    '.a{color:white}', '@media (min-width:1px){.a{outline:1px solid Black}}', '.a{border-color:rgb(0 0 0 / .2)}']) {
    assert.equal(colorLiterals(css).length, 1, css);
  }
  for (const css of ['#download{scroll-margin-top:var(--h)}', '#abc, #def{color:var(--t)}', '.a{white-space:nowrap}',
    '/* color: #fff */ .a{color:var(--t)}', '@media (min-width:1px){#fab{color:transparent}}']) {
    assert.deepEqual(colorLiterals(css), [], css);
  }
});

test('Only tokens.css holds color literals', () => {
  for (const name of PAGE_CSS) {
    assert.deepEqual(colorLiterals(read(name)), [], `${name} has a color literal`);
    assert.doesNotMatch(stripComments(read(name)), /@font-face/, `${name} declares a font`);
  }
});

test('The Neutral tokens match the app value for value', () => {
  const appearance = stripComments(read('appearance.css', appUi));
  const app = declarations(appearance.match(/\[data-theme="neutral"\]\{([^}]*)\}/)[1]);
  const tokens = stripComments(read('tokens.css'));
  const neutral = declarations(tokens.match(/:root\{([^}]*)\}/)[1]);
  const own = new Set(['--f-display', '--f-body', '--f-data', '--video', '--on-video', '--cue-bg']);
  const shared = Object.keys(neutral).filter(name => !own.has(name));
  assert.ok(shared.length >= 30, 'the Neutral block is copied, not summarized');
  for (const name of shared) assert.equal(neutral[name], app[name], name);
  assert.match(neutral['--f-body'], /^'Geist',/);
  assert.match(neutral['--f-data'], /^'Geist Mono',/);
  assert.match(tokens, /color-scheme:dark/);
});

test('Every custom property a stylesheet reads is defined', () => {
  const defined = new Set();
  const used = new Map();
  for (const name of ['tokens.css', ...PAGE_CSS]) {
    const css = stripComments(read(name));
    for (const m of css.matchAll(/(--[\w-]+)\s*:/g)) defined.add(m[1]);
    for (const m of css.matchAll(/var\((--[\w-]+)/g)) used.set(m[1], name);
  }
  for (const [property, file] of used) assert.ok(defined.has(property), `${file} reads undefined ${property}`);
});

test('Geist is self-hosted from copies of the app fonts, with swap', () => {
  const tokens = read('tokens.css');
  const faces = [...tokens.matchAll(/@font-face\{([^}]*)\}/g)].map(m => m[1]);
  assert.deepEqual(faces.map(face => face.match(/font-family:'([^']+)'/)[1]), ['Geist', 'Geist Mono']);
  for (const face of faces) {
    assert.match(face, /src:url\('\/fonts\/[a-z-]+\.woff2'\) format\('woff2'\)/);
    assert.match(face, /font-display:swap/);
  }
  const fonts = new URL('fonts/', site);
  assert.deepEqual(readdirSync(fonts).sort(), ['OFL-Geist.txt', 'OFL-GeistMono.txt', 'geist-mono.woff2', 'geist.woff2']);
  for (const name of readdirSync(fonts)) {
    assert.ok(readFileSync(new URL(name, fonts)).equals(readFileSync(new URL(`fonts/${name}`, appUi))), `${name} matches desktop/ui/fonts`);
  }
});

test('Every page preloads Geist, loads tokens.css first and matches the Neutral background', () => {
  const bg = declarations(stripComments(read('tokens.css')).match(/:root\{([^}]*)\}/)[1])['--bg'];
  for (const page of PAGES) {
    const html = read(page);
    const preload = html.indexOf('<link rel="preload" href="/fonts/geist.woff2" as="font" type="font/woff2" crossorigin />');
    const tokens = html.indexOf('<link rel="stylesheet" href="/tokens.css" />');
    const styles = html.indexOf('<link rel="stylesheet" href="/styles.css" />');
    assert.ok(preload > 0 && preload < tokens && tokens < styles, `${page}: preload, tokens.css, styles.css`);
    assert.equal(html.split('rel="stylesheet"')[1].includes('href="/tokens.css"'), true, `${page}: tokens.css is the first stylesheet`);
    assert.match(html, /<meta name="color-scheme" content="dark" \/>/, page);
    assert.match(html, new RegExp(`<meta name="theme-color" content="${bg}" />`), page);
    assert.doesNotMatch(html, /#171717|Segoe UI/, page);
  }
});
