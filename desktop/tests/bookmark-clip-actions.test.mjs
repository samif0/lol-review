import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const source = await readFile(new URL('../ui/vodplayer.js', import.meta.url), 'utf8');
const helpers = source.slice(source.indexOf('// Saved-moment card actions.'), source.indexOf('\nfunction renderMoments()'));
const plain = value => JSON.parse(JSON.stringify(value));

function node() {
  const classes = new Set();
  return { dataset: {}, children: [], value: '', textContent: '', attributes: {},
    classList: { add: name => classes.add(name), contains: name => classes.has(name) },
    appendChild(child) { this.children.push(child); },
    setAttribute(name, value) { this.attributes[name] = value; },
  };
}

function fixture({ bookmark = { id: 9, gameTimeSeconds: 40, note: 'Watch the support', objectiveId: 7, promptId: 3 },
  duration = 200, origin = 0, filePath = 'fixture.mp4', editedNote } = {}) {
  const ids = new Map(), steps = [], picks = [];
  const $ = id => { if (!ids.has(id)) ids.set(id, node()); return ids.get(id); };
  const vod = { filePath, gameDurationSeconds: duration, gameTimeAtVideoStart: origin, bookmarks: [bookmark] };
  const context = vm.createContext({ $, _vod: vod, _clipIn: -1, _clipOut: -1, _clipQuality: 'bad', _clipObjUserSet: false,
    _T: { duration, pause() { steps.push('pause'); } },
    document: { createElement: node, querySelector() { return editedNote === undefined ? null : { value: editedNote }; } },
    seekTo(seconds) { steps.push(['seek', seconds]); }, renderClipState() { steps.push('render'); },
    openClipTools(options) { steps.push(['open', plain(options)]); },
    clipHint(message, error) { steps.push(['hint', message, error]); },
    fillObjectivePromptPicker(el, objectiveId, promptId) { picks.push({ objectiveId, promptId }); },
  });
  vm.runInContext(`${helpers}\n globalThis.hooks = { bookmarkClipRange, renderBookmarkCardActions, makeClipFromBookmark,
    draft: () => ({ start: _clipIn, end: _clipOut, quality: _clipQuality, userSet: _clipObjUserSet }) };`, context);
  return { $, vod, bookmark, steps, picks, hooks: context.hooks };
}

test('bookmark clip range includes 15 seconds either side and respects available recording boundaries', () => {
  const { hooks } = fixture();
  for (const [time, duration, origin, expected] of [
    [40, 200, 0, { start: 25, end: 55 }],
    [4, 200, 0, { start: 0, end: 19 }],
    [196, 200, 0, { start: 181, end: 200 }],
    [40, 200, 35, { start: 35, end: 55 }],
    [40, 0, 0, { start: 25, end: 55 }],
    [0, 1, 0, { start: 0, end: 1 }],
    [210, 200, 0, { start: 195, end: 200 }],
    [240, 200, 0, null], [1, 0.8, 0, null], [10, 200, 30, null],
    [-1, 200, 0, null], [NaN, 200, 0, null],
  ]) assert.deepEqual(plain(hooks.bookmarkClipRange(time, duration, origin)), expected);
});

test('Make clip copies the live bookmark note and tag, then focuses the paused draft without deleting its bookmark', () => {
  const f = fixture({ editedNote: '  Updated note before blur finished  ' });
  const original = structuredClone(f.bookmark);
  assert.equal(f.hooks.makeClipFromBookmark('9'), true);
  assert.deepEqual(plain(f.hooks.draft()), { start: 25, end: 55, quality: '', userSet: true });
  assert.equal(f.$('vp-clip-note').value, '  Updated note before blur finished  ');
  assert.deepEqual(f.picks, [{ objectiveId: 7, promptId: 3 }]);
  assert.deepEqual(f.steps.slice(0, 4), ['pause', ['seek', 25], 'render', ['open', { focusNote: true }]]);
  assert.deepEqual(f.vod.bookmarks, [original]);
});

test('Make clip keeps source note when no inline edit exists and rejects unavailable sources', () => {
  const f = fixture();
  assert.equal(f.hooks.makeClipFromBookmark(9), true);
  assert.equal(f.$('vp-clip-note').value, 'Watch the support');
  for (const invalid of [0, -1, 1.5, 'invalid', 8]) {
    const unavailable = fixture();
    assert.equal(unavailable.hooks.makeClipFromBookmark(invalid), false);
    assert.equal(unavailable.steps.length, 0, 'invalid identities do not open or mutate a draft');
  }
  assert.equal(fixture({ filePath: '' }).hooks.makeClipFromBookmark(9), false);
  assert.equal(fixture({ bookmark: { id: 9, hasClip: true, gameTimeSeconds: 40 } }).hooks.makeClipFromBookmark(9), false);
  const outside = fixture({ duration: 10, origin: 0 });
  assert.equal(outside.hooks.makeClipFromBookmark(9), false);
  assert.deepEqual(plain(outside.hooks.draft()), { start: -1, end: -1, quality: 'bad', userSet: false });
  assert.equal(outside.steps.at(-1)[2], true);
});

test('bookmark cards expose Make clip and text Delete; clip cards keep the clip-specific delete target', () => {
  const f = fixture(), actions = node(), deletion = node();
  const row = { querySelector: selector => selector === '.vp-bm-edit' ? actions : selector === '.vp-bm-del' ? deletion : null };
  f.hooks.renderBookmarkCardActions(row, { id: 9, editable: true, isClip: false });
  const make = actions.children[0];
  assert.equal(make.textContent, 'Make clip');
  assert.deepEqual(plain(make.dataset), { action: 'make_clip_from_bookmark', bmId: '9' });
  assert.equal(make.disabled, false);
  assert.equal(deletion.textContent, 'Delete');
  assert.equal(deletion.attributes['aria-label'], 'Delete bookmark');
  assert.equal(deletion.classList.contains('vp-moment-delete'), true);
  const clipDelete = node(); clipDelete.dataset.shareBmId = '90';
  f.hooks.renderBookmarkCardActions({ querySelector: selector => selector === '.vp-clipdel-btn' ? clipDelete : null }, { isClip: true });
  assert.equal(clipDelete.textContent, 'Delete');
  assert.equal(clipDelete.attributes['aria-label'], 'Delete clip');
  assert.equal(clipDelete.dataset.shareBmId, '90');
  f.vod.filePath = '';
  f.hooks.renderBookmarkCardActions(row, { id: 9, editable: true, isClip: false });
  assert.equal(actions.children.at(-1).disabled, true);
});
