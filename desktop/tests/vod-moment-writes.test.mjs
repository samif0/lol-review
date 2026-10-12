import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { renderList } from '../ui/vodcorrections-panel.js';
import { clipboardTimers, fixture } from './vod-player-fixture.mjs';

// Saved-moment rows on the VOD page: notes, tags and deletes. The fixture follows
// DOM propagation and browser focus, so a refresh that destroys the field being
// typed into (or a listener that swallows Enter) fails here.
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const settle = () => new Promise(resolve => setImmediate(resolve));

function bookmarkRows(options = {}) {
  const f = fixture(options);
  f.snapshot.bookmarks = [{ id: 1, gameTimeSeconds: 10, note: 'First' }, { id: 2, gameTimeSeconds: 20, note: 'Second' }];
  f.hooks.setFilter('bm');
  f.hooks.renderMoments();
  const row = id => f.$('vp-bookmarks').querySelector(`[data-bm-id="${id}"]`);
  const field = id => row(id)?.querySelector('.vp-bm-editnote');
  return { ...f, row, field };
}

function evidenceRows(options = {}) {
  const f = fixture(options);
  f.snapshot.autoMoments = [{ id: 41, startTimeSeconds: 30, endTimeSeconds: 40, note: 'Auto note', objectiveId: 7 }];
  f.hooks.renderMoments();
  const row = () => f.$('vp-bookmarks').querySelector('[data-moment-key="ev:41"]');
  return { ...f, row };
}

const status = row => row.querySelector('.vp-moment-status');

test('a note saved on blur keeps the field the user moved into, its text, focus and shortcuts', async () => {
  const save = deferred();
  const f = bookmarkRows({ invokeWrite: command => command === 'update_bookmark_note' ? save.promise : { ok: true } });
  const first = f.field(1), second = f.field(2);
  first.focus();
  await f.type(' changed');
  second.focus(); // blur on the first note starts its save
  await f.type(' more');
  assert.deepEqual(f.writes, [{ command: 'update_bookmark_note', args: { payload: { bookmarkId: 1, note: 'First changed' } } }]);
  f.snapshot.bookmarks[0].note = 'First changed';
  save.resolve({ ok: true });
  await f.hooks.flush();
  await settle(); await settle();
  assert.equal(f.reads.length, 1, 'a confirmed save refreshes the list');
  // Identity checks use ok(): a failing equal() would print the whole DOM graph.
  assert.ok(f.field(2) === second, 'the focused row is kept, not rebuilt');
  assert.ok(f.document.activeElement === second, 'focus stays in the note being typed');
  assert.equal(second.value, 'Second more');
  assert.equal(f.row(1).querySelector('.vp-bm-note').textContent, 'First changed', 'other rows show the saved note');
  await f.type('bios');
  assert.equal(second.value, 'Second morebios', 'letters keep landing in the note');
  assert.equal(f.$('vp-composer').hidden, true, 'B never reaches the bookmark shortcut');
  assert.equal(f.hooks.captureVodViewState().clip.start, -1, 'I never reaches the clip shortcut');
  assert.equal(f.writes.length, 1);
});

test('an unchanged note never writes on blur and a refresh keeps unsaved text in rebuilt rows', async () => {
  const f = bookmarkRows();
  const first = f.field(1);
  first.focus(); first.blur();
  first.value = '  First  ';
  first.focus(); first.blur();
  await f.hooks.flush();
  assert.equal(f.writes.length, 0, 'whitespace-only edits are the saved note');
  f.field(2).value = 'Unsaved second';
  f.hooks.renderMoments();
  assert.notEqual(f.field(2), undefined);
  assert.equal(f.field(2).value, 'Unsaved second', 'a rebuild keeps text that was not saved yet');
  assert.equal(f.field(1).value, '  First  ');
});

test('Enter saves a moment note once and leaves the caret in the field', async () => {
  const f = bookmarkRows();
  const first = f.field(1);
  first.focus(); first.value = 'Pressed Enter';
  const enter = await f.emit('keydown', first, { key: 'Enter' });
  await f.hooks.flush();
  assert.equal(enter.defaultPrevented, true);
  assert.deepEqual(f.writes.map(write => write.args.payload.note), ['Pressed Enter']);
  assert.ok(f.document.activeElement === first, 'Enter does not drop focus onto the page shortcuts');
  assert.ok(f.field(1) === first);
  await f.emit('keydown', first, { key: 'Enter' });
  await f.emit('keydown', first, { key: 'Enter', repeat: true });
  first.blur();
  await f.hooks.flush();
  assert.equal(f.writes.length, 1, 'a saved note is not written again');

  const e = evidenceRows();
  const note = e.row().querySelector('.vp-ev-note');
  note.focus(); note.value = '  Evidence note  ';
  await e.emit('keydown', note, { key: 'Enter' });
  await e.hooks.flush();
  assert.equal(e.writes.length, 1);
  assert.equal(e.writes[0].command, 'save_pattern_moment_note');
  assert.equal(e.writes[0].args.payload.text, 'Evidence note');
  assert.equal(e.writes[0].args.payload.evidenceId, 41);
});

test('a failed note save stays in the field with a visible error and Retry', async () => {
  let fail = true;
  const f = bookmarkRows({ invokeWrite: () => { if (fail) throw new Error('Sidecar unavailable'); return { ok: true }; } });
  const first = f.field(1);
  first.focus(); first.value = 'Do not lose this';
  first.blur();
  await f.hooks.flush();
  assert.equal(f.reads.length, 0, 'a failed write never refreshes over the draft');
  assert.equal(first.value, 'Do not lose this');
  assert.equal(status(f.row(1)).hidden, false);
  assert.equal(status(f.row(1)).querySelector('.vp-moment-status-txt').textContent, 'Couldn’t save this note.');
  f.hooks.renderMoments(); // an unrelated refresh keeps the draft and the error
  assert.equal(f.field(1).value, 'Do not lose this');
  assert.equal(status(f.row(1)).hidden, false);
  fail = false;
  f.snapshot.bookmarks[0].note = 'Do not lose this';
  await f.emit('click', status(f.row(1)).querySelector('.vp-moment-retry'));
  await f.hooks.flush();
  assert.deepEqual(f.writes.map(write => write.args.payload.note), ['Do not lose this', 'Do not lose this']);
  assert.equal(status(f.row(1)).hidden, true, 'a confirmed retry clears the error');
  assert.equal(f.reads.length, 1);
});

test('a failed objective tag keeps the choice on screen and Retry sends what the picker shows', async () => {
  let fail = true;
  const f = evidenceRows({ invokeWrite: command => (fail && command === 'set_evidence_objective' ? { ok: false, error: 'Objective missing' } : { ok: true }) });
  const picker = () => f.row().querySelector('.vp-ev-obj');
  assert.equal(picker().value, 'obj:7');
  picker().value = 'obj:11';
  await f.emit('change', picker());
  await f.hooks.flush();
  assert.equal(f.writes.length, 1, 'the prompt write waits for a confirmed objective');
  assert.equal(picker().value, 'obj:11');
  assert.equal(status(f.row()).querySelector('.vp-moment-status-txt').textContent, 'Couldn’t save this objective tag.');
  f.hooks.renderMoments();
  assert.equal(picker().value, 'obj:11', 'a refresh keeps the unsaved choice');
  fail = false;
  await f.emit('click', status(f.row()).querySelector('.vp-moment-retry'));
  await f.hooks.flush();
  assert.deepEqual(f.writes.slice(1).map(write => [write.command, write.args.payload.objectiveId ?? write.args.payload.promptId]),
    [['set_evidence_objective', 11], ['set_evidence_prompt', null]]);
  assert.equal(status(f.row()).hidden, true);
});

test('failed deletes stay on their row with Retry and a refused clip delete re-enables its button', async () => {
  let fail = true;
  const f = bookmarkRows({ invokeWrite: () => { if (fail) throw new Error('Offline'); return { ok: true }; } });
  await f.emit('click', f.row(2).querySelector('.vp-bm-del'));
  await f.hooks.flush();
  assert.equal(f.confirmations.length, 1);
  assert.equal(status(f.row(2)).querySelector('.vp-moment-status-txt').textContent, 'Couldn’t delete this bookmark.');
  fail = false;
  f.snapshot.bookmarks = f.snapshot.bookmarks.filter(bookmark => bookmark.id !== 2);
  await f.emit('click', status(f.row(2)).querySelector('.vp-moment-retry'));
  await f.hooks.flush();
  assert.ok(f.row(2) === null, 'a confirmed delete removes the row');
  assert.deepEqual(f.writes.map(write => write.command), ['delete_bookmark', 'delete_bookmark']);

  const c = fixture({ invokeWrite: () => ({ ok: false, error: 'Clip in use' }) });
  c.snapshot.bookmarks = [{ id: 302, hasClip: true, gameTimeSeconds: 50, note: '' }];
  c.hooks.setFilter('clips'); c.hooks.renderMoments();
  const row = c.$('vp-bookmarks').querySelector('[data-moment-key="bm:302"]');
  const button = row.querySelector('.vp-clipdel-btn');
  await c.emit('click', button);
  await c.hooks.flush();
  assert.equal(button.disabled, false);
  assert.equal(status(row).hidden, false);
  assert.equal(status(row).querySelector('.vp-moment-status-txt').textContent, 'Couldn’t delete this clip.');
  assert.equal(c.reads.length, 0);
});

test('moment rows are plain containers whose Watch button owns the jump', async () => {
  const f = bookmarkRows();
  const row = f.row(1);
  assert.equal(row.dataset.action, undefined);
  assert.equal(row.getAttribute('role'), null);
  await f.emit('click', row.querySelector('.vp-bm-note'));
  await f.emit('click', f.field(1));
  assert.deepEqual(f.seeks, [], 'reading or editing a row never seeks');
  const watch = row.querySelector('.ricon');
  assert.equal(watch.tagName, 'BUTTON');
  assert.equal(watch.getAttribute('aria-label'), 'Watch moment at 0:10');
  await f.emit('click', watch);
  assert.deepEqual(f.seeks, [10]);

  const html = await readFile(new URL('../ui/vodplayer.html', import.meta.url), 'utf8');
  for (const id of ['tpl-bookmark', 'tpl-fix-row']) {
    const template = html.slice(html.indexOf(`<template id="${id}">`), html.indexOf('</template>', html.indexOf(`<template id="${id}">`)));
    const rowTag = template.match(/<div class="(?:moment|vp-fix-row)"[^>]*>/)[0];
    assert.doesNotMatch(rowTag, /role=|tabindex=|data-action=/, `${id} row must not be an interactive container`);
    assert.match(template, /<button type="button" class="[^"]*" data-action="jump"/, `${id} has a separate jump button`);
  }
});

test('correction rows put the jump on their time button', () => {
  const part = (tag, className) => ({ tagName: tag, className, dataset: {}, textContent: '', hidden: false, classList: { add() {} },
    attributes: {}, setAttribute(name, value) { this.attributes[name] = value; } });
  const rows = [];
  const tpl = () => {
    const parts = { '.vp-fix-when': part('BUTTON', 'vp-fix-when'), '.vp-fix-what': part('SPAN'), '.vp-fix-state': part('SPAN'),
      '.vp-fix-reason': part('SPAN'), '.vp-fix-revert': part('BUTTON') };
    const row = { dataset: {}, querySelector: selector => parts[selector], parts };
    rows.push(row);
    return row;
  };
  const host = { children: [], appendChild(row) { this.children.push(row); } };
  renderList(host, [{ correctionId: 'c1', state: 'active', gameTimeSeconds: 75, timeLabel: '1:15', summary: 'Moved' }],
    { tpl, show: (el, on) => { el.hidden = !on; }, clear: el => { el.children = []; }, clock: String, labelOf: String });
  const when = rows[0].parts['.vp-fix-when'];
  assert.equal(when.dataset.seconds, '75');
  assert.equal(when.attributes['aria-label'], 'Watch corrected event at 1:15');
  assert.equal(rows[0].dataset.correctionId, 'c1');
});

test('sidebar navigation and Update & restart wait for a held nudge and the focused note', async () => {
  const save = deferred(), nudge = deferred();
  const f = bookmarkRows({ invokeWrite: () => save.promise });
  assert.equal(typeof f.window.revuBeforeNavigate, 'function');
  f.corrections.heldNudge = nudge.promise;
  const first = f.field(1);
  first.focus(); first.value = 'Typed, then clicked the sidebar';
  let left = null;
  const leaving = f.window.revuBeforeNavigate().then(value => { left = value; });
  await settle();
  assert.equal(f.corrections.pendingFlushes, 1, 'the corrections nudge is flushed');
  assert.deepEqual(f.writes.map(write => write.args.payload.note), ['Typed, then clicked the sidebar']);
  nudge.resolve();
  await settle();
  assert.equal(left, null, 'the note write is still in flight');
  save.resolve({ ok: true });
  await leaving;
  assert.equal(left, true);
});

test('a correction refresh that arrives during an in-flight reload runs once more afterwards', async () => {
  const timers = clipboardTimers(), replies = [];
  const f = fixture({ timers, invokeReply: () => new Promise(resolve => replies.push(resolve)) });
  const corrected = f.windowListener('revu:events-corrected');
  corrected({ detail: { gameId: 42 } });
  timers.advance(300);
  assert.equal(replies.length, 1);
  corrected({ detail: { gameId: 42 } });
  timers.advance(300);
  assert.equal(replies.length, 1, 'never two reloads at once');
  replies[0](f.snapshot);
  await settle(); await settle();
  assert.equal(replies.length, 2, 'the event that arrived mid-reload is not dropped');
  replies[1](f.snapshot);
  await settle();
  corrected({ detail: { gameId: 7 } });
  timers.advance(300);
  assert.equal(replies.length, 2, 'other games are ignored');
});

test('trade events label their own tier on an unframed timeline', () => {
  const f = fixture({ objectives: [], events: [
    { id: 1, gameTimeSeconds: 40, eventType: 'TRADE', kind: 'neutral', label: 'Trade' },
    { id: 2, gameTimeSeconds: 120, eventType: 'SHORT_TRADE', kind: 'neutral', label: 'Short trade' },
  ] });
  f.hooks.placeMarkers(200);
  const bars = f.$('vp-markers').children.filter(bar => bar.classList.contains('evbar-trade'));
  assert.equal(bars.length, 2);
  assert.deepEqual(bars.map(bar => bar.querySelector('.evbar-code')?.textContent), ['TRD', 'STR']);
});

test('the recording loads through the transport once per render and errors show plain copy', async () => {
  const f = fixture();
  f.hooks.render({ ...f.snapshot, filePath: 'match.mp4', hasVod: true }, f.core);
  f.hooks.render({ ...f.snapshot, filePath: 'match.mp4', hasVod: true }, f.core);
  const video = f.$('vp-video');
  assert.equal(video.listeners.get('error').length, 1, 'a refresh replaces the error listener instead of stacking it');
  assert.equal(video.listeners.get('loadedmetadata').length, 1);
  assert.equal(f.transport.loadOptions.autoplay, false);
  assert.equal(f.transport.loadOptions.startSeconds, null, 'loading never seeks on its own');
  await f.emit('error', video);
  const detail = f.$('err-detail').textContent;
  assert.equal(detail, 'Revu could not play this recording. It may have been moved or deleted.');
  assert.doesNotMatch(detail, /revu-media|asset scope|src=/);
  assert.equal(f.$('errpanel').hidden, false);
});
