// The shared game page's side column (vod-side.js) on the page: the viewer's own bookmarks and
// clips (B, I and O, kept in this browser and sent as a link), the sharer's notes, and the
// events with their filter, one at a time behind the Notes and Events switch.
import assert from 'node:assert/strict';
import test from 'node:test';
import { DOC, finishedMeta, openPage } from './vod-harness.mjs';

const META = 'GET /vod-meta/Abc1234';
// The same game, shared as a single MP4: its moments seek and play the video directly.
function mp4Meta(doc = DOC) {
  const { format, playable, media_seconds, ...rest } = finishedMeta({ meta: doc });
  return { ...rest, content_type: 'video/mp4', size_bytes: 1 };
}

function memoryStorage() {
  const map = new Map();
  return { map, get length() { return map.size; }, key: i => [...map.keys()][i] ?? null, getItem: k => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => { map.set(k, String(v)); }, removeItem: k => { map.delete(k); } };
}

async function game({ doc = DOC, hash = '', storage = memoryStorage() } = {}) {
  const run = openPage({ hash, routes: { [META]: mp4Meta(doc) }, globals: { localStorage: storage } });
  await run.advance(0);
  assert.equal(run.state(), 'state-game');
  return { ...run, storage, rows: id => run.$(id).children.map(li => li.children[0].children.map(c => c.textContent)) };
}

const text = (run, value) => { run.$('vod-composer-text').value = value; };
const enter = run => run.press('Enter', { target: run.$('vod-composer-text') });

test('B bookmarks the game clock on screen; Enter saves it to the list, the lane and this browser', async () => {
  const run = await game();
  assert.deepEqual([run.$('vod-review').hidden, run.$('vod-mine-empty').hidden, run.$('vod-notes-count').textContent], [false, false, '1']);
  run.video.currentTime = 100;
  assert.equal(run.press('b').defaultPrevented, true);
  assert.deepEqual([run.$('vod-composer').hidden, run.$('vod-composer-when').textContent], [false, 'Bookmark at 1:40.']);
  assert.equal(run.win.focused, run.$('vod-composer-text'), 'the note takes the typing');
  assert.equal(run.press('i', { target: run.$('vod-composer-text') }).defaultPrevented, false, 'typing I in the note is typing');
  text(run, 'Ward the river before this fight');
  assert.equal(enter(run).defaultPrevented, true);
  assert.equal(run.$('vod-composer').hidden, true);
  assert.deepEqual(run.rows('vod-mine-list'), [['1:40', 'Bookmark', 'Ward the river before this fight']]);
  assert.equal(run.$('vod-mine-list').children[0].children[1].getAttribute('aria-label'), 'Delete bookmark at 1:40');
  assert.deepEqual([run.$('vod-mine-empty').hidden, run.$('vod-share').hidden, run.$('vod-notes-count').textContent], [true, false, '2']);
  assert.ok(run.mark('Bookmark at 1:40'), 'a diamond on the lane');
  assert.deepEqual(JSON.parse(run.storage.getItem('revu.notes.v.Abc1234')).notes, [{ t: 100, end: null, text: 'Ward the river before this fight' }]);

  // The same game opened again in this browser shows the note again.
  const again = await game({ storage: run.storage });
  assert.deepEqual(again.rows('vod-mine-list'), [['1:40', 'Bookmark', 'Ward the river before this fight']]);
});

test('I starts a clip and the lane shows it growing; O ends it; the saved clip plays to its end and pauses', async () => {
  const run = await game();
  run.video.currentTime = 200;
  run.press('i');
  assert.deepEqual([run.$('vod-composer-when').textContent, run.$('vod-add-clip-label').textContent, run.$('vod-add-clip-key').textContent],
    ['Clip from 3:20. Press O to end it.', 'End clip', 'O']);
  const band = run.$('vod-lane-range');
  assert.deepEqual([band.hidden, band.style.left], [false, '20%']);
  run.playTo(230);
  assert.equal(band.style.width, '3%', 'the band follows the playhead until O');
  run.press('o');
  assert.equal(run.$('vod-composer-when').textContent, 'Clip from 3:20 to 3:50.');
  text(run, 'Good trade, then back');
  enter(run);
  assert.equal(band.hidden, true);
  assert.deepEqual(run.rows('vod-mine-list'), [['3:20', 'Clip', '30s', 'Good trade, then back']]);
  assert.equal(run.mark('Clip at 3:20').className, 'vod-mark is-clip tier-note');

  run.$('vod-mine-list').children[0].children[0].click();
  assert.deepEqual([run.video.currentTime, run.video.paused], [200, false]);
  run.playTo(229);
  assert.equal(run.video.paused, false);
  run.playTo(230.3);
  assert.equal(run.video.paused, true, 'the clip ends where it was made to');
});

test('a deleted note can come back with Undo; the delete button names the note', async () => {
  const run = await game();
  run.video.currentTime = 61;
  run.press('b');
  text(run, 'first');
  enter(run);
  run.video.currentTime = 400;
  run.press('b');
  enter(run);
  assert.deepEqual(run.rows('vod-mine-list'), [['1:01', 'Bookmark', 'first'], ['6:40', 'Bookmark']]);
  run.$('vod-mine-list').children[0].children[1].click();
  assert.deepEqual(run.rows('vod-mine-list'), [['6:40', 'Bookmark']]);
  const status = run.$('vod-notes-status');
  assert.deepEqual(status.children.map(c => c.textContent), ['Note deleted.', 'Undo']);
  assert.equal(run.win.focused, status.children[1], 'the keyboard lands on Undo');
  status.children[1].click();
  assert.deepEqual(run.rows('vod-mine-list'), [['1:01', 'Bookmark', 'first'], ['6:40', 'Bookmark']]);
  assert.deepEqual(status.children, []);
  assert.equal(JSON.parse(run.storage.getItem('revu.notes.v.Abc1234')).notes.length, 2);
});

test('a link with notes adds them for this browser, says so, and leaves the address bar clean', async () => {
  const notes = JSON.stringify([[30, 'Their jungler shows top'], [312, 330, 'Dragon fight']]);
  const run = await game({ hash: '#notes=j' + Buffer.from(notes).toString('base64url') });
  assert.deepEqual(run.rows('vod-mine-list'), [['0:30', 'Bookmark', 'Their jungler shows top'], ['5:12', 'Clip', '18s', 'Dragon fight']]);
  assert.equal(run.$('vod-notes-status').children[0].textContent, 'Added 2 notes from the link.');
  assert.deepEqual(run.context.history.replaced, ['https://revu.lol/v/Abc1234']);
  assert.equal(JSON.parse(run.storage.getItem('revu.notes.v.Abc1234')).notes.length, 2);

  const broken = await game({ hash: '#notes=j%%%' });
  assert.equal(broken.$('vod-notes-status').children[0].textContent, "The notes in this link couldn't be read.");
  assert.deepEqual(broken.rows('vod-mine-list'), []);
});

test('Notes and Events show one at a time; the events filter counts each kind and narrows the list', async () => {
  const doc = { ...DOC, markers: [{ t: 30, kind: 'KILL', label: 'Kill' }, { t: 90, kind: 'DEATH', label: 'Death' },
    { t: 312, kind: 'DRAGON', label: 'Dragon' }, { t: 600, kind: 'TURRET', label: 'Turret' }, { t: 700, kind: 'RECALL', label: 'Recall' }] };
  const run = await game({ doc });
  assert.deepEqual([run.$('vod-side-tabs').hidden, run.$('vod-panel-notes').hidden, run.$('vod-panel-events').hidden], [false, false, true]);
  assert.equal(run.$('vod-events-count').textContent, '5');
  run.$('vod-tab-events').click();
  assert.deepEqual([run.$('vod-panel-notes').hidden, run.$('vod-panel-events').hidden], [true, false]);
  assert.deepEqual([run.$('vod-tab-notes').getAttribute('aria-pressed'), run.$('vod-tab-events').getAttribute('aria-pressed')], ['false', 'true']);
  const filters = run.$('vod-event-filter').children;
  assert.deepEqual(filters.map(b => [b.children[0].textContent, b.children[1].textContent, b.hidden, b.getAttribute('aria-pressed')]),
    [['All', '5', false, 'true'], ['Deaths', '1', false, 'false'], ['Kills', '1', false, 'false'], ['Objectives', '2', false, 'false']]);
  assert.deepEqual(run.rows('vod-events-list').map(r => r[2]), ['Kill', 'Death', 'Dragon', 'Turret', 'Recall']);
  assert.equal(run.$('vod-events-list').children[0].children[0].children[1].className, 'vod-event-dot is-kill', 'time, color, name');
  filters[3].click();
  assert.deepEqual(run.rows('vod-events-list').map(r => [r[0], r[2]]), [['5:12', 'Dragon'], ['10:00', 'Turret']]);
  assert.equal(filters[3].getAttribute('aria-pressed'), 'true');
  assert.equal(run.marks().length, 6, 'the lane keeps every moment');

  // B from the Events side opens the Notes, where the note is written.
  run.press('b');
  assert.deepEqual([run.$('vod-panel-notes').hidden, run.$('vod-composer').hidden], [false, false]);
  run.press('Escape');
  assert.equal(run.$('vod-composer').hidden, true, 'Esc drops the draft');

  const kills = await game({ doc: { ...DOC, markers: [{ t: 30, kind: 'KILL' }, { t: 60, kind: 'KILL' }] } });
  assert.equal(kills.$('vod-event-filter').hidden, true, 'one kind of event: nothing to filter');
  const quiet = await game({ doc: { ...DOC, markers: [], notes: [] } });
  assert.deepEqual([quiet.$('vod-side-tabs').hidden, quiet.$('vod-panel-notes').hidden, quiet.$('vod-notes').hidden], [true, false, false],
    'no events: the notes alone, no switch');
});

test('blocked storage keeps the notes for the page\'s life', async () => {
  const blocked = { get length() { throw new Error('SecurityError'); }, getItem() { throw new Error('SecurityError'); },
    setItem() { throw new Error('SecurityError'); }, removeItem() { throw new Error('SecurityError'); }, key() { throw new Error('SecurityError'); } };
  const run = await game({ storage: blocked });
  run.video.currentTime = 5;
  run.press('b');
  enter(run);
  assert.deepEqual(run.rows('vod-mine-list'), [['0:05', 'Bookmark']]);
});
