import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

// House copy rules for the Review and VOD review surfaces: no em dashes in any
// string the page can show, and no emoji-capable glyphs as transport icons.
// Comments are free to use either; only string and template literals are read.
const SCRIPTS = ['vodplayer.js', 'vodtransport.js', 'vodcorrections.js', 'vodcorrections-panel.js', 'review.js',
  'clip-narration.mjs', 'narration-capture.mjs', 'clip-share-wait.mjs', 'clip-transcript.mjs',
  'timeline-event-hit.mjs', 'vod-view-state.mjs', 'video-timeline.mjs'];
const PAGES = ['vodplayer.html', 'review.html'];
const TRANSPORT_GLYPHS = /[⏮⏭⛶❚]|\u{1F50A}|\u{1F507}|\u{1F5D7}|\p{Emoji_Presentation}/u;

const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');

function stringLiterals(fileName, text) {
  const source = ts.createSourceFile(fileName, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const found = [];
  const visit = node => {
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node) || ts.isTemplateHead(node)
      || ts.isTemplateMiddle(node) || ts.isTemplateTail(node)) {
      const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
      found.push({ text: node.text, line: line + 1 });
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return found;
}

test('Review and VOD scripts never put an em dash in a string', async () => {
  for (const name of SCRIPTS) {
    const offenders = stringLiterals(name, await read(name)).filter(literal => literal.text.includes('—'));
    assert.deepEqual(offenders.map(literal => `${name}:${literal.line} ${literal.text}`), []);
  }
});

test('Review and VOD pages show no em dash outside comments', async () => {
  for (const name of PAGES) {
    const visible = (await read(name)).replace(/<!--[\s\S]*?-->/g, '');
    assert.equal(visible.includes('—'), false, `${name} shows an em dash`);
  }
});

test('the video transport uses SVG icons, not emoji-capable glyphs', async () => {
  for (const literal of stringLiterals('vodtransport.js', await read('vodtransport.js'))) {
    assert.doesNotMatch(literal.text, TRANSPORT_GLYPHS, `vodtransport.js:${literal.line}`);
  }
  const html = await read('vodplayer.html');
  const transport = html.slice(html.indexOf('<div class="vp-transport">'), html.indexOf('<!-- event timeline'));
  assert.doesNotMatch(transport, TRANSPORT_GLYPHS);
  assert.doesNotMatch(transport, /▶/, 'play is an SVG icon too');
  assert.equal((transport.match(/<span class="tbtn-icon" aria-hidden="true" data-icon="[a-z]+"><svg /g) || []).length, 5);
});
