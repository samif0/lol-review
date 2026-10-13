import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, plain } from './vod-player-fixture.mjs';

// The calm VOD side column and timeline: meaning tones instead of hex, the focus
// picker, the header details, the note time chip and the Journal thread. All of it
// renders from data already on the page and never writes.

const texts = el => el.children.map(child => child.textContent);

function withPicker(f) {
  const column = f.$('vp-review-column');
  const picker = column.appendChild(f.document.createElement('details'));
  picker.className = 'vp-focus-picker';
  const summary = picker.appendChild(f.document.createElement('summary'));
  picker.appendChild(f.$('vp-objbar'));
  return { picker, summary };
}

test('event rows read time, name and summary with a tone dot; the ordinal stays in the accessible name', () => {
  const f = fixture({ objectives: [], events: [
    { id: 1, gameTimeSeconds: 765, eventType: 'DEATH', label: 'Death', summary: 'Lee Sin', kind: 'loss', colorHex: '#f3a3a8' },
    { id: 2, gameTimeSeconds: 612, eventType: 'DRAGON', label: 'Dragon', summary: 'Infernal', kind: 'gold', colorHex: '#f3c794' },
    { id: 3, gameTimeSeconds: 900, eventType: 'TEAMFIGHT', label: 'Teamfight', kind: 'teamfight-away', teamfight: { self: 'away' } },
  ] });
  f.hooks.renderMoments();
  const rows = f.$('vp-event-list').children;
  assert.deepEqual(rows.map(row => row.children.map(part => part.className)),
    rows.map(() => ['vp-event-time', 'vp-event-title', 'vp-event-dot']));
  assert.deepEqual(rows.map(row => row.querySelector('.vp-event-time').textContent), ['10:12', '12:45', '15:00']);
  assert.deepEqual(rows.map(row => row.querySelector('.vp-event-title').textContent), ['Dragon · Infernal', 'Death · Lee Sin', 'Teamfight']);
  assert.deepEqual(rows.map(row => row.querySelector('.vp-event-dot').dataset.tone), ['dragon', 'death', 'away']);
  assert.ok(rows.every(row => row.classList.contains('ui-row') && row.tagName === 'BUTTON'));
  assert.equal(rows[1].getAttribute('aria-label'), 'Watch event 2: Death · Lee Sin at 12:45');
  assert.equal(f.$('vp-review-objective').textContent, 'Match events');
  assert.equal(f.$('vp-focus-kind').textContent, 'Whole match');
  assert.equal(f.$('vp-event-count').textContent, '3 events');
  assert.equal(f.writes.length, 0);
});

test('the focus picker names the frame, closes after a choice and returns focus to its summary', () => {
  const f = fixture({ events: [{ id: 1, gameTimeSeconds: 10, objectiveId: 7 }, { id: 2, gameTimeSeconds: 30, objectiveId: 11 }] });
  const { picker, summary } = withPicker(f);
  f.hooks.renderObjBar(); f.hooks.wireFraming(); f.hooks.renderMoments();
  assert.equal(f.$('vp-review-objective').textContent, 'Plan the fight');
  assert.equal(f.$('vp-focus-kind').textContent, 'Your focus');
  assert.equal(f.$('vp-event-count').textContent, '1 event');
  assert.equal(summary.getAttribute('aria-disabled'), null);
  picker.open = true;
  const second = f.$('vp-objbar').children[1]; second.focus();
  f.$('vp-objbar').emit('click', { target: second });
  assert.equal(f.hooks.focused, 11);
  assert.equal(picker.open, false, 'a choice closes the picker');
  assert.equal(f.document.activeElement, summary, 'focus returns to the summary, not the page');
  assert.equal(f.$('vp-review-objective').textContent, 'Keep a safe position');
  assert.ok(f.$('vp-objbar').children.every(tab => tab.classList.contains('ui-row')));
  assert.equal(f.seeks.length, 0); assert.equal(f.writes.length, 0);
});

test('Escape inside the open focus picker closes it without reaching the page shortcuts', async () => {
  const f = fixture({ events: [{ id: 1, gameTimeSeconds: 10, objectiveId: 7 }] });
  const { picker, summary } = withPicker(f);
  f.hooks.renderObjBar(); f.hooks.wireFraming();
  picker.open = true;
  const tab = f.$('vp-objbar').children[0]; tab.focus();
  const escape = await f.emit('keydown', tab, { key: 'Escape' });
  assert.equal(escape.defaultPrevented, true);
  assert.equal(picker.open, false);
  assert.equal(f.document.activeElement, summary);
  assert.equal(f.hooks.focused, 7, 'closing is not a choice');
  const again = await f.emit('keydown', summary, { key: 'Escape' });
  assert.equal(again.defaultPrevented, undefined, 'a closed picker leaves Escape to the page');
});

test('with the note composer docked, Escape in the open focus picker closes only the picker', async () => {
  const f = fixture({ events: [{ id: 1, gameTimeSeconds: 10, objectiveId: 7 }] });
  const { picker, summary } = withPicker(f);
  f.hooks.renderObjBar(); f.hooks.wireFraming();
  f.transport.currentTime = 42;
  await f.emit('keydown', f.$('vp-video'), { key: 'b' });
  assert.equal(f.$('vp-composer').hidden, false, 'B docks the note composer');
  assert.equal(f.$('vp-note-when').textContent, '0:42');
  picker.open = true;
  summary.focus();
  const onSummary = await f.emit('keydown', summary, { key: 'Escape' });
  assert.equal(onSummary.defaultPrevented, true);
  assert.equal(picker.open, false, 'the picker closes');
  assert.equal(f.$('vp-composer').hidden, false, 'the composer stays docked');
  assert.equal(f.$('vp-note-when').classList.contains('is-pinned'), true, 'the captured bookmark time is kept');
  assert.equal(f.document.activeElement, summary, 'focus stays on the picker, not the play button');
  picker.open = true;
  const tab = f.$('vp-objbar').children[0]; tab.focus();
  await f.emit('keydown', tab, { key: 'Escape' });
  assert.equal(picker.open, false);
  assert.equal(f.$('vp-composer').hidden, false);
  await f.emit('keydown', summary, { key: 'Escape' });
  assert.equal(f.$('vp-composer').hidden, true, 'with the picker closed, Escape closes the composer again');
});

test('without objectives the picker is a plain heading that never opens', async () => {
  const f = fixture({ objectives: [], events: [{ id: 1, gameTimeSeconds: 10, eventType: 'KILL' }] });
  const { picker, summary } = withPicker(f);
  picker.open = true;
  f.hooks.wireFraming(); f.hooks.renderMoments();
  assert.equal(picker.open, false);
  assert.equal(picker.classList.contains('is-static'), true);
  assert.equal(summary.getAttribute('aria-disabled'), 'true');
  const click = await f.emit('click', summary);
  assert.equal(click.defaultPrevented, true);
});

test('timeline bars carry meaning tones, focused codes take two rows instead of overprinting, and ticks and legend follow the data', () => {
  const f = fixture({ events: [
    { id: 1, gameTimeSeconds: 560, eventType: 'RECALL', kind: 'recall', shortLabel: 'RCL', objectiveIds: [7], colorHex: '#a9c8ff' },
    { id: 2, gameTimeSeconds: 612, eventType: 'DRAGON', kind: 'gold', shortLabel: 'DRG', objectiveIds: [7],
      colorHex: '#f3c794', objectiveColorHex: '#c89b3c' },
    { id: 3, gameTimeSeconds: 765, eventType: 'DEATH', kind: 'loss', shortLabel: 'DTH', colorHex: '#f3a3a8' },
  ] });
  f.snapshot.bookmarks = [{ id: 1, gameTimeSeconds: 323, note: 'Note' }, { id: 2, gameTimeSeconds: 761, hasClip: true }];
  f.hooks.placeMarkers(1931);
  const bars = f.$('vp-markers').children.filter(bar => bar.classList.contains('evbar'));
  assert.deepEqual(bars.map(bar => bar.dataset.tone), ['recall', 'dragon', undefined], 'dim ticks stay neutral');
  assert.ok(bars.every(bar => bar.style['--evc'] === undefined), 'no inline color');
  const codes = bars.map(bar => bar.querySelector('.evbar-code'));
  assert.equal(codes[0].classList.contains('evbar-code-alt'), false);
  assert.equal(codes[1].classList.contains('evbar-code-alt'), true, 'the second close focused code rises a row');
  const marks = f.$('vp-markers').children.filter(mark => mark.classList.contains('ev-bm'));
  assert.deepEqual(marks.map(mark => mark.dataset.tone), ['note', 'clip']);
  const ticks = f.$('vp-tl-axis').children;
  assert.deepEqual(texts(f.$('vp-tl-axis')), ['0:00', '10:00', '20:00', '30:00']);
  assert.equal(ticks[0].classList.contains('is-start'), true);
  assert.deepEqual(f.$('vp-tl-sub').children.map(item => [item.dataset.tone, item.textContent]),
    [['dragon', 'Dragon'], ['recall', 'Recall'], ['clip', 'Clip'], ['note', 'Note']]);
  f.hooks.placeMarkers(840);
  assert.deepEqual(texts(f.$('vp-tl-axis')), ['0:00', '5:00', '10:00'], 'short games tick every five minutes');
  f.hooks.placeMarkers(0);
  assert.equal(f.$('vp-tl-axis').children.length, 0);
  assert.equal(f.$('vp-tl-sub').children.length, 0);
});

test('saved rows wear a clip or note diamond, suggestions a polarity dot, and only the player\'s own note reads in their voice', () => {
  const f = fixture();
  f.snapshot.bookmarks = [
    { id: 2, gameTimeSeconds: 761, timeLabel: '12:41', note: '', hasClip: true, clipStartSeconds: 750, clipEndSeconds: 775 },
    { id: 9, gameTimeSeconds: 40, note: 'Mine' },
  ];
  f.snapshot.autoMoments = [{ id: 31, startTimeSeconds: 180, endTimeSeconds: 200, timeLabel: '3:00', title: 'Early death',
    note: '', polarity: 'bad', polarityColorHex: '#f3a3a8' }];
  f.hooks.setFilter('clips'); f.hooks.renderMoments();
  const clip = f.$('vp-bookmarks').children[0];
  assert.equal(clip.querySelector('.vp-bm-time').textContent, '12:30 to 12:55');
  assert.deepEqual(plain(clip.querySelector('.vp-bm-mark').dataset), { tone: 'clip', shape: 'diamond' });
  assert.equal(clip.querySelector('.vp-bm-note').classList.contains('own-words'), false, 'a generated clip title is not the player\'s words');
  f.hooks.setFilter('auto'); f.hooks.renderMoments();
  const auto = f.$('vp-bookmarks').children[0];
  assert.deepEqual(plain(auto.querySelector('.vp-bm-mark').dataset), { tone: 'loss', shape: 'dot' });
  assert.equal(auto.querySelector('.vp-bm-src').dataset.tone, 'loss');
  assert.equal(auto.querySelector('.vp-bm-src').style.color, undefined, 'no inline polarity hex');
  f.hooks.setFilter('bm'); f.hooks.renderMoments();
  const note = f.$('vp-bookmarks').children[0];
  assert.deepEqual(plain(note.querySelector('.vp-bm-mark').dataset), { tone: 'note', shape: 'diamond' });
  assert.equal(note.querySelector('.vp-bm-note').classList.contains('own-words'), true);
  assert.equal(f.writes.length, 0);
});

test('a refresh keeps the focused row and still updates its title when it becomes the player\'s own note', () => {
  const f = fixture();
  f.snapshot.bookmarks = [{ id: 9, gameTimeSeconds: 40, note: '' }];
  f.hooks.setFilter('bm'); f.hooks.renderMoments();
  const row = f.$('vp-bookmarks').children[0];
  row.querySelector('.vp-bm-editnote').focus();
  f.snapshot.bookmarks = [{ id: 9, gameTimeSeconds: 40, note: 'Now written' }];
  f.hooks.renderMoments();
  assert.equal(f.$('vp-bookmarks').children[0], row, 'the focused row stays in place');
  assert.equal(row.querySelector('.vp-bm-note').textContent, 'Now written');
  assert.equal(row.querySelector('.vp-bm-note').classList.contains('own-words'), true);
});

test('the header shows the result word in its tone, the length and the KDA from a matching review read only', async () => {
  const f = fixture();
  Object.assign(f.snapshot, { resultText: 'Victory', gameMode: 'Ranked Solo/Duo', gameDurationSeconds: 1931 });
  f.hooks.renderContextDetails();
  const details = f.$('vp-ctx-details');
  assert.deepEqual(texts(details), ['Victory', 'Ranked Solo/Duo', '32:11']);
  assert.equal(details.children[0].className, 'vp-result');
  assert.equal(details.children[0].dataset.result, 'win');
  await f.hooks.readReviewSummary(async () => ({ subject: { gameId: 99, header: { kdaText: '1 / 9 / 1' } } }), 42);
  assert.equal(f.hooks.reviewSummary, null, 'another game\'s review is ignored');
  await f.hooks.readReviewSummary(() => { throw new Error('offline'); }, 42);
  assert.equal(f.hooks.reviewSummary, null, 'a failed read changes nothing');
  await f.hooks.readReviewSummary(async () => ({ subject: { gameId: 42, header: { kdaText: '8 / 2 / 11' },
    form: { reviewNotes: '  Respect her range  ' } } }), 42);
  assert.deepEqual(texts(details), ['Victory', 'Ranked Solo/Duo', '32:11', '8 / 2 / 11']);
  for (const [word, result] of [['Defeat', 'loss'], ['Remake', 'remake']]) {
    f.snapshot.resultText = word; f.hooks.renderContextDetails();
    assert.equal(details.children[0].dataset.result, result);
  }
  const takeaway = f.$('vp-thread-list').children.at(-1);
  assert.equal(takeaway.dataset.kind, 'takeaway');
  assert.equal(takeaway.querySelector('.vp-th-text').textContent, 'Respect her range');
  assert.equal(f.writes.length, 0);
});

test('the Journal thread merges focus events, notes and clips in game time and follows selection and playback', async () => {
  const f = fixture({ events: [{ id: 1, eventKey: 'death:20', gameTimeSeconds: 20, objectiveId: 7, eventType: 'DEATH', label: 'Death', summary: 'Graves' }] });
  f.snapshot.bookmarks = [{ id: 5, gameTimeSeconds: 10, note: 'Ward first' }];
  f.snapshot.savedClips = [{ id: 41, hasClip: true, startTimeSeconds: 30, endTimeSeconds: 45, note: '', title: 'Caught out', objectiveId: 7 }];
  f.snapshot.autoMoments = [{ id: 31, startTimeSeconds: 25, note: '' }, { id: 32, startTimeSeconds: 26, note: 'My words' }];
  f.hooks.renderMoments();
  const list = f.$('vp-thread-list');
  assert.deepEqual(list.children.map(li => li.dataset.kind), ['now', 'note', 'event', 'note', 'clip']);
  assert.equal(f.$('vp-thread-count').textContent, '2 notes · 1 clip');
  const clipText = list.children[4].querySelector('.vp-th-text');
  assert.equal(clipText.textContent, 'Caught out');
  assert.equal(clipText.classList.contains('own-words'), false);
  assert.equal(list.children[1].querySelector('.vp-th-text').classList.contains('own-words'), true);
  assert.equal(list.children.some(li => li.dataset.kind === 'takeaway'), false, 'no takeaway row before the review read answers');
  const eventRow = list.children[2].querySelector('[data-review-key]');
  assert.equal(eventRow.dataset.reviewKey, 'key:death:20');
  await f.emit('click', eventRow);
  assert.deepEqual(f.seeks, [10]);
  assert.equal(eventRow.classList.contains('is-on'), true);
  assert.equal(eventRow.getAttribute('aria-current'), 'true');
  assert.equal(f.$('vp-event-list').children[0].getAttribute('aria-current'), 'true');
  await f.emit('click', list.children[1].querySelector('[data-action="jump"]'));
  assert.equal(f.seeks.at(-1), 10);
  assert.equal(eventRow.classList.contains('is-on'), false, 'jumping to a note clears the selected event');
  f.transport.currentTime = 22; f.hooks.tickLiveTime();
  assert.deepEqual(list.children.map(li => li.dataset.kind), ['note', 'event', 'now', 'note', 'clip']);
  assert.equal(list.children[2].querySelector('.vp-th-t').textContent, '0:22');
  assert.equal(f.$('vp-note-when').textContent, '0:22');
  assert.equal(f.writes.length, 0);
});

test('a confirmed missing takeaway offers the review; the link navigates through match navigation', async () => {
  const f = fixture();
  f.snapshot.bookmarks = [{ id: 5, gameTimeSeconds: 10, note: 'Ward first' }];
  await f.hooks.readReviewSummary(async () => ({ subject: { gameId: 42, header: { kdaText: '' }, form: { reviewNotes: '' } } }), 42);
  f.hooks.renderMoments();
  const last = f.$('vp-thread-list').children.at(-1);
  assert.equal(last.classList.contains('is-missing'), true);
  assert.ok(last.querySelector('.vp-th-link'));
});

test('an older review whose takeaway lives in focus next shows it in the thread, as Home quotes it', async () => {
  const f = fixture();
  f.snapshot.bookmarks = [{ id: 5, gameTimeSeconds: 10, note: 'Ward first' }];
  await f.hooks.readReviewSummary(async () => ({ subject: { gameId: 42, header: { kdaText: '' },
    form: { reviewNotes: '', focusNext: '  Track the jungler before shoving  ' } } }), 42);
  assert.equal(f.hooks.reviewSummary.takeaway, 'Track the jungler before shoving');
  f.hooks.renderMoments();
  const last = f.$('vp-thread-list').children.at(-1);
  assert.equal(last.dataset.kind, 'takeaway');
  assert.equal(last.classList.contains('is-missing'), false);
  assert.equal(last.querySelector('.vp-th-text').textContent, 'Track the jungler before shoving');
  assert.equal(f.writes.length, 0);
});

test('the note time chip follows playback, pins to a captured bookmark time and releases it when the draft is abandoned', async () => {
  const f = fixture();
  f.transport.currentTime = 42; f.hooks.tickLiveTime();
  const chip = f.$('vp-note-when');
  assert.equal(chip.textContent, '0:42'); assert.equal(chip.classList.contains('is-pinned'), false);
  await f.emit('keydown', f.$('vp-video'), { key: 'b' });
  assert.equal(chip.textContent, '0:42'); assert.equal(chip.classList.contains('is-pinned'), true);
  f.transport.currentTime = 75; f.hooks.tickLiveTime();
  assert.equal(chip.textContent, '0:42', 'a captured moment keeps its time while playback moves');
  await f.emit('keydown', f.$('vp-bm-note'), { key: 'Escape' });
  assert.equal(chip.textContent, '1:15'); assert.equal(chip.classList.contains('is-pinned'), false);
  assert.equal(f.writes.length, 0);
});

test('Journal starts the saved moments list closed because the thread already shows them', () => {
  const f = fixture();
  f.document.documentElement = { dataset: { theme: 'journal' } };
  f.snapshot.bookmarks = [{ id: 5, gameTimeSeconds: 10, note: 'Ward first' }];
  f.hooks.renderMoments();
  assert.equal(f.$('vp-saved-moments').open, false);
});
