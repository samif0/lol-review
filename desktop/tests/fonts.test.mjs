import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { FONT_PRELOADS } from '../ui/appearance.mjs';
import { bundledFonts } from '../scripts/package.mjs';

// The app ships five subset variable fonts under ui/fonts, declared once in
// appearance.css and loaded from the app itself, never from the network.
const ui = new URL('../ui/', import.meta.url);
const fonts = new URL('fonts/', ui);
const read = name => readFileSync(new URL(name, ui), 'utf8');
const withoutComments = css => css.replace(/\/\*[\s\S]*?\*\//g, '');
const css = withoutComments(read('appearance.css'));
const faces = [...css.matchAll(/@font-face\s*\{([^}]*)\}/g)].map(([, body]) => body);
const sources = faces.map(body => body.match(/src:\s*([^;]+)/)?.[1].trim());
const FILES = ['geist-mono.woff2', 'geist.woff2', 'instrument-sans.woff2', 'source-serif-4-italic.woff2', 'source-serif-4-roman.woff2'];
const LICENSES = ['OFL-Geist.txt', 'OFL-GeistMono.txt', 'OFL-InstrumentSans.txt', 'OFL-SourceSerif4.txt'];

test('appearance.css declares exactly five local woff2 faces with font-display block', () => {
  assert.equal(faces.length, 5);
  const files = [];
  for (const [index, body] of faces.entries()) {
    const match = /^url\('\.\/fonts\/([a-z0-9-]+\.woff2)'\) format\('woff2'\)$/.exec(sources[index]);
    assert.ok(match, `local woff2 source: ${sources[index]}`);
    files.push(match[1]);
    assert.match(body, /font-display:\s*block/);
    assert.match(body, /font-family:\s*'[^']+'/);
    assert.match(body, /font-weight:\s*\d{3}(?:\s+\d{3})?/);
  }
  assert.deepEqual(files.sort(), FILES);
  assert.deepEqual([...bundledFonts].map(name => name.replace('ui/fonts/', '')).sort(), FILES, 'packaging requires the same faces');
  for (const source of sources) assert.doesNotMatch(source, /https?:|\/\/|data:|local\(/);
});

test('no other stylesheet declares fonts or loads anything remote', () => {
  for (const file of readdirSync(ui).filter(name => name.endsWith('.css'))) {
    const text = withoutComments(read(file));
    if (file !== 'appearance.css') assert.doesNotMatch(text, /@font-face/, file);
    assert.doesNotMatch(text, /@import|url\(\s*['"]?(?:https?:)?\/\//i, file);
  }
});

test('the font folder holds the five faces and four OFL texts, all used and within budget', () => {
  assert.deepEqual(readdirSync(fonts).sort(), [...LICENSES, ...FILES].sort());
  let total = 0;
  for (const file of FILES) {
    const bytes = readFileSync(new URL(file, fonts));
    assert.equal(bytes.subarray(0, 4).toString('latin1'), 'wOF2', `${file} is woff2`);
    total += statSync(new URL(file, fonts)).size;
  }
  assert.ok(total <= 260 * 1024, `fonts total ${total} B`);
  for (const theme of Object.keys(FONT_PRELOADS)) {
    for (const name of FONT_PRELOADS[theme]) assert.ok(FILES.includes(`${name}.woff2`), `${theme} preloads ${name}`);
  }
});

test('each family ships its SIL Open Font License with its copyright line', () => {
  const body = text => text.slice(text.indexOf('This Font Software is licensed under the SIL Open Font License, Version 1.1.'));
  const reference = body(readFileSync(new URL('OFL-SourceSerif4.txt', fonts), 'utf8'));
  assert.match(reference, /SIL OPEN FONT LICENSE Version 1\.1 - 26 February 2007/);
  assert.match(reference, /TERMINATION/);
  const owners = { 'OFL-SourceSerif4.txt': 'Source Serif 4', 'OFL-InstrumentSans.txt': 'Instrument Sans',
    'OFL-Geist.txt': 'Geist', 'OFL-GeistMono.txt': 'Geist' };
  for (const file of LICENSES) {
    const text = readFileSync(new URL(file, fonts), 'utf8');
    assert.match(text.split('\n')[0], new RegExp(`^Copyright 20\\d\\d The ${owners[file]} Project Authors`), file);
    assert.equal(body(text), reference, `${file} carries the full OFL 1.1 text`);
  }
});

test('every font family a theme names has a face, with system fallbacks', () => {
  const families = new Set(faces.map(body => body.match(/font-family:\s*'([^']+)'/)[1]));
  assert.deepEqual([...families].sort(), ['Geist', 'Geist Mono', 'Instrument Sans', 'Source Serif 4']);
  for (const [, stack] of css.matchAll(/--f-(?:display|body|data|voice):([^;}]+)/g)) {
    const first = stack.match(/^'([^']+)'/)?.[1];
    assert.ok(families.has(first), `${stack} starts with a bundled face`);
    assert.match(stack, /,(?:Georgia,serif|'Segoe UI',system-ui,sans-serif|Consolas,monospace)$/, stack);
  }
  // Source Serif 4 has a roman and an italic face; the rest are roman only.
  assert.equal(faces.filter(body => /font-style:\s*italic/.test(body)).length, 1);
});
