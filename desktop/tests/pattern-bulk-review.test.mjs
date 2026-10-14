import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { BULK_ADVANCE_MS, BULK_COPY, bulkQueue, createBulkRun } from '../ui/pattern-bulk.mjs';
import { fixture, playback, pattern, snapshot } from './patterns-page-fixture.mjs';

// Back-to-back review of a ready mistake trend: "Watch all N" plays only the moments
// since the last review, oldest first, skips what cannot play, moves on a beat after
// each moment ends, stops on Pause, and marks nothing until Finish review.

function bookmark(n, extra = {}) {
  return { evidenceId: `evidence-${n}`, gameId: 100 + n, ordinal: n, title: `Moment ${n}`, bookmarkId: 200 + n,
    sourceKind: 'bookmark', note: '', hasNote: false, polarity: 'bad', hasVod: true, vodPath: `vod-${n}.mp4`,
    startTimeSeconds: n * 100, endTimeSeconds: n * 100 + 30, gameTimeAtVideoStart: 0, ...extra };
}

// A ready trend of six moments. The sidecar queued 2, 3, 5 and 6 (saved since the last
// review); moment 3 has since lost its recording and kept no clip.
function readyTrend(key = 'trend-a', title = 'Track the jungler') {
  return { ...pattern(key), title, readyToReview: true, reviewMomentOrdinals: [2, 3, 5, 6],
    moments: [1, 2, 3, 4, 5, 6].map(n => bookmark(n, n === 3 ? { hasVod: false, vodPath: '' } : {})) };
}

function withPlay(f) {
  const p = playback(f);
  p.transport.play = () => p.calls.push('play');
  return p;
}

const windowEnd = (f, transport) => {
  transport.currentTime = f.hooks.activeMoment().endTimeSeconds;
  f.hooks.constrainPlayback();
};

test('the queue holds only the listed moments that can play, oldest first, and only for a ready trend', () => {
  const trend = readyTrend();
  const canPlay = m => !!m.hasVod;
  assert.deepEqual(bulkQueue(trend, canPlay), [1, 4, 5]);
  assert.deepEqual(bulkQueue({ ...trend, reviewMomentOrdinals: [6, 2, 2, 99, 0, 'x'] }, canPlay), [1, 5]);
  assert.deepEqual(bulkQueue({ ...trend, readyToReview: false }, canPlay), []);
  assert.deepEqual(bulkQueue({ ...trend, reviewMode: 'saved' }, canPlay), []);
  assert.deepEqual(bulkQueue(null), []);
});

test('a run moves on one beat after a moment ends, holds on Pause and ends after the last', () => {
  const timers = [], shown = [];
  let changes = 0;
  const run = createBulkRun({ key: 'k', queue: [1, 4], show: index => shown.push(index), onChange: () => { changes += 1; },
    schedule: (fn, ms) => timers.push({ fn, ms }), cancel: id => { timers[id - 1].fn = null; } });
  run.start();
  assert.deepEqual(shown, [1]);
  run.ended(); run.ended();
  assert.equal(timers.length, 1, 'the end of a moment schedules one advance');
  assert.equal(timers[0].ms, BULK_ADVANCE_MS);
  run.paused();
  assert.equal(timers[0].fn, null, 'Pause stops the advance');
  run.ended();
  assert.equal(timers.length, 1, 'a held run does not move on');
  run.next();
  assert.deepEqual(shown, [1, 4], 'Play after the end moves on at once');
  assert.equal(run.held, false);
  run.ended();
  timers.at(-1).fn();
  assert.equal(run.done, true);
  assert.deepEqual(shown, [1, 4]);
  assert.ok(changes > 0);
});

test('Watch all plays only the moments since the last review, oldest first, and skips what cannot play', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend()]));
  assert.equal(f.$('pat-bulk').hidden, false);
  assert.equal(f.$('pat-bulk-start').textContent, 'Watch all 3 back to back');
  assert.equal(f.$('pat-bulk-text').textContent, '3 mistakes across 3 games are ready to review together.');
  assert.equal(f.$('pat-pick').children[0].querySelector('.pat-card-state').textContent, 'Ready to review');
  const { loads, transport } = withPlay(f);

  await f.click('pat-bulk-start', 'bulk_start');
  assert.equal(f.hooks.activeMoment().title, 'Moment 2');
  assert.equal(loads.length, 1);
  assert.equal(loads[0].startSeconds, 200);
  assert.equal(loads[0].autoplay, true);
  assert.equal(f.$('m-position').textContent, 'Moment 1 of 3');
  assert.equal(f.$('pat-bulk-text').textContent, 'Watching all 3 back to back');
  assert.equal(f.$('pat-bulk-help').textContent, BULK_COPY.playing);
  assert.equal(f.$('pat-bulk-start').hidden, true);
  assert.equal(f.$('pat-bulk-progress').textContent, 'Moment 1 of 3', 'the live region carries the position');
  assert.equal(f.$('pat-markrev').hidden, true, 'finishing waits for the last moment');
  assert.equal(f.$('pat-pending-text').textContent, 'Finish review appears after the last moment.');

  windowEnd(f, transport);
  assert.deepEqual(f.timers.filter(t => t.fn).map(t => t.ms), [1000], 'the next moment waits one beat');
  assert.equal(f.beat(), 1);
  assert.equal(f.hooks.activeMoment().title, 'Moment 5', 'moment 3 cannot play and moment 4 was reviewed before');
  assert.equal(f.$('m-position').textContent, 'Moment 2 of 3');
  assert.equal(f.$('pat-bulk-progress').textContent, 'Moment 2 of 3');
  assert.equal(loads.at(-1).startSeconds, 500);
  assert.equal(f.writes.length, 0);
});

test('Pause stops the advance, and Play after the window moves on to the next moment', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend()]));
  const { loads, transport, video } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');

  // Paused partway: the window end never comes, and nothing moves on.
  video.ended = false;
  f.hooks.onVideoPause();
  assert.equal(f.$('pat-bulk-help').textContent, BULK_COPY.paused);
  windowEnd(f, transport);
  assert.equal(f.beat(), 0, 'a paused run does not advance');
  assert.equal(f.hooks.activeMoment().title, 'Moment 2');

  // Pause during the beat after the window also stops the advance.
  f.hooks.onVideoPlay();
  assert.equal(f.hooks.activeMoment().title, 'Moment 5', 'Play past the end continues the run');
  windowEnd(f, transport);
  f.hooks.onVideoPause();
  assert.equal(f.beat(), 0);
  assert.equal(f.hooks.activeMoment().title, 'Moment 5');

  // Switching sources is not a pause the player chose (no metadata yet).
  f.hooks.onVideoPlay();
  assert.equal(f.hooks.activeMoment().title, 'Moment 6');
  video.readyState = 0;
  f.hooks.onVideoPause();
  video.readyState = 1;
  assert.equal(f.hooks.review.state.held, false);
  assert.equal(loads.length, 3);
});

test('after the last moment the takeaway asks what keeps repeating and Finish review clears the nudge', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend(), { ...readyTrend('trend-b', 'Ward before crossing'), severity: 'medium' }]));
  const { calls, transport } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  for (let i = 0; i < 3; i++) { windowEnd(f, transport); f.beat(); }

  assert.deepEqual(f.hooks.review.state.watched, { key: 'trend-a', length: 3, watched: 3 });
  assert.equal(calls.at(-1), 'pause', 'the last moment stops instead of running on');
  // The bar keeps its place (the player does not jump) and asks the review's question.
  assert.equal(f.$('pat-bulk').hidden, false);
  assert.equal(f.$('pat-bulk-text').textContent, 'You watched all 3. What keeps repeating?');
  assert.equal(f.$('pat-bulk-help').textContent, BULK_COPY.doneHelp);
  assert.equal(f.$('pat-bulk-progress').textContent, '');
  assert.equal(f.$('m-note-label').textContent, 'What keeps repeating?');
  assert.equal(f.$('m-note').getAttribute('aria-label'), 'You watched all 3. What keeps repeating?');
  assert.equal(f.$('m-note').focused, true);
  assert.equal(f.$('pat-markrev').hidden, false);
  assert.equal(f.$('pat-markrev').textContent, 'Finish review');
  assert.equal(f.$('pat-pending-text').hidden, true, 'the bar carries the instruction');
  assert.equal(f.writes.length, 0, 'watching marks nothing');

  f.$('m-note').value = 'I cross the river before I know where the jungler is.';
  await f.click('pat-markrev', 'mark_reviewed');
  assert.deepEqual(f.writes.map(w => w.command), ['save_pattern_moment_note', 'mark_pattern_reviewed']);
  assert.equal(f.writes[1].args.payload.patternKey, 'trend-a');
  assert.deepEqual(f.announced, ['revu:patterns-changed'], 'the sidebar count asks again at once');
  assert.equal(f.hooks.activePattern().patternKey, 'trend-a', 'the finished trend stays open');
  assert.equal(f.$('pat-review-status').hidden, false);
  assert.equal(f.$('pat-review-status').textContent,
    'Reviewed. Revu will bring this back if it keeps happening. Another pattern is ready: Ward before crossing.');
  assert.equal(f.$('pat-pending-text').hidden, true, 'one confirmation, not two');
  assert.equal(f.$('pat-bulk-next').hidden, false);
  assert.equal(f.$('pat-bulk-next').focused, true);
  assert.equal(f.$('m-note-label').textContent, 'What would you do differently in this moment?');
  assert.equal(f.$('m-quality-good').disabled, false, 'the finished trend stays open to revisit');

  await f.click('pat-bulk-next', 'bulk_next');
  assert.equal(f.hooks.activePattern().patternKey, 'trend-b');
  assert.equal(f.hooks.review.state.key, 'trend-b');
  assert.equal(f.$('m-position').textContent, 'Moment 1 of 3');
  assert.equal(f.$('pat-review-status').hidden, true);
  assert.equal(f.$('pat-pending-text').hidden, false);
});

test('leaving a run midway marks nothing', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend(), pattern('other')]));
  const { transport } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  windowEnd(f, transport);
  f.beat();
  await f.hooks.markReviewed();
  assert.equal(f.writes.length, 0, 'Finish is not offered midway');

  f.hooks.selectPattern(1);
  assert.equal(f.hooks.review.state.key, null);
  assert.equal(f.beat(), 0);
  f.hooks.selectPattern(0);
  assert.equal(f.$('pat-bulk-start').hidden, false, 'the trend is still ready to watch');
  assert.equal(f.$('pat-markrev').textContent, 'Finish reviewing this pattern');
  assert.deepEqual(f.writes, []);
  assert.deepEqual(f.announced, []);

  // Stepping to a moment outside the queue also ends the run.
  await f.click('pat-bulk-start', 'bulk_start');
  f.hooks.gotoMoment(0);
  assert.equal(f.hooks.review.state.key, null);
  assert.equal(f.$('pat-bulk-start').hidden, false);
});

test('a queued moment that fails to load is skipped after the beat', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend()]));
  const { loads } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  loads[0].onError();
  assert.match(f.$('m-nstatus').textContent, /Could not load this clip/);
  assert.equal(f.beat(), 1);
  assert.equal(f.hooks.activeMoment().title, 'Moment 5');
  assert.equal(f.$('m-position').textContent, 'Moment 2 of 3');
});

test('collections and trends that are not ready never offer Watch all', () => {
  const f = fixture();
  f.hooks.render(snapshot([pattern('t'), { ...readyTrend('s'), reviewMode: 'saved' }]));
  assert.equal(f.$('pat-bulk').hidden, true);
  f.hooks.selectView('saved');
  assert.equal(f.$('pat-bulk').hidden, true);
});

test("Home's link opens its pattern, and a first visit opens a trend that is ready", () => {
  const linked = fixture(undefined, { search: '?pattern=trend-b' });
  linked.hooks.render(snapshot([readyTrend(), readyTrend('trend-b', 'Ward before crossing')]));
  assert.equal(linked.hooks.activePattern().patternKey, 'trend-b');
  assert.equal(linked.$('pat-review-title').focused, true);

  const first = fixture();
  first.hooks.render(snapshot([pattern('not-ready'), readyTrend('ready')]));
  assert.equal(first.hooks.activePattern().patternKey, 'ready');
});

test('the copy stays plain', async () => {
  const code = await readFile(new URL('../ui/pattern-bulk.mjs', import.meta.url), 'utf8');
  assert.doesNotMatch(code, /[–—]/);
  assert.equal(BULK_COPY.start(5), 'Watch all 5 back to back');
  assert.equal(BULK_COPY.progress(2, 5), 'Moment 2 of 5');
  assert.equal(BULK_COPY.done(5), 'You watched all 5. What keeps repeating?');
  assert.equal(BULK_COPY.reviewed, 'Reviewed. Revu will bring this back if it keeps happening.');
});

test('rating a moment during a run keeps the end of the review, and Finish refreshes the trends itself', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend(), readyTrend('trend-b', 'Ward before crossing')]));
  const { transport } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  await f.hooks.setMomentQuality('neutral');
  assert.equal(f.$('pat-refresh-trends').hidden, false);
  for (let i = 0; i < 3; i++) { windowEnd(f, transport); f.beat(); }

  assert.equal(f.$('pat-markrev').textContent, 'Finish review');
  assert.equal(f.$('pat-markrev').disabled, false, 'a rating made during the run does not block Finish');

  // Updating the trends first keeps the watched review and its question.
  f.$('pat-refresh-trends').disabled = false;
  await f.click('pat-refresh-trends', 'reload_patterns');
  assert.equal(f.reads.length, 1);
  assert.equal(f.$('pat-bulk-text').textContent, 'You watched all 3. What keeps repeating?');
  assert.equal(f.$('m-note-label').textContent, 'What keeps repeating?');
  assert.equal(f.$('pat-markrev').textContent, 'Finish review');

  await f.click('pat-markrev', 'mark_reviewed');
  assert.equal(f.writes.at(-1).command, 'mark_pattern_reviewed');
  assert.equal(f.hooks.activePattern().patternKey, 'trend-a');
  assert.equal(f.$('pat-review-status').textContent,
    'Reviewed. Revu will bring this back if it keeps happening. Another pattern is ready: Ward before crossing.');
  assert.equal(f.$('pat-bulk-next').hidden, false);
});

test('Finish after a rating during the run marks the review, then updates the trends', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend()]));
  const { transport } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  await f.hooks.setMomentQuality('good');
  for (let i = 0; i < 3; i++) { windowEnd(f, transport); f.beat(); }
  await f.click('pat-markrev', 'mark_reviewed');
  assert.deepEqual(f.writes.map(w => w.command), ['set_bookmark_quality', 'mark_pattern_reviewed']);
  assert.equal(f.reads.length, 1, 'the trends were refreshed after finishing');
  assert.equal(f.$('pat-refresh-trends').hidden, true);
  assert.equal(f.$('pat-review-status').textContent, BULK_COPY.reviewed);
});

test('a step held back by a rating that fails to save leaves the run on the moment shown', async () => {
  const f = fixture(async command => command === 'set_bookmark_quality' ? { ok: false, error: 'busy' } : { ok: true });
  f.hooks.render(snapshot([readyTrend()]));
  const { transport } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  windowEnd(f, transport);
  const rating = f.hooks.setMomentQuality('good');
  assert.equal(f.beat(), 1, 'the advance fires while the rating saves');
  windowEnd(f, transport);
  assert.equal(f.timers.filter(t => t.fn).length, 0, 'no second step while one is held back');
  await rating;
  assert.equal(f.hooks.activeMoment().title, 'Moment 2');
  assert.equal(f.$('m-position').textContent, 'Moment 1 of 3');
  windowEnd(f, transport);
  f.beat();
  assert.equal(f.hooks.activeMoment().title, 'Moment 5', 'the next step skips nothing');
  assert.equal(f.$('m-position').textContent, 'Moment 2 of 3');
});

test('Play on a kept clip that ended replays it instead of being cut off', async () => {
  const f = fixture();
  const clipOnly = n => bookmark(n, { hasVod: false, vodPath: '', hasClip: true, clipPath: `clip-${n}.mp4` });
  f.hooks.render(snapshot([{ ...readyTrend(), moments: [1, 2, 3, 4, 5, 6].map(clipOnly) }]));
  const { video, loads } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  assert.equal(loads[0].url, 'synthetic-media:clip-2.mp4');
  video.ended = true;
  f.hooks.onVideoPause();
  f.hooks.onVideoEnded();
  assert.equal(f.hooks.review.state.waiting, true);
  video.ended = false;
  f.hooks.onVideoPlay();
  assert.equal(f.beat(), 0, 'the replay is not cut off');
  assert.equal(f.hooks.activeMoment().title, 'Moment 2');
  video.ended = true;
  f.hooks.onVideoEnded();
  assert.equal(f.beat(), 1);
  assert.equal(f.hooks.activeMoment().title, 'Moment 3');
});

test('writing a takeaway holds the run on its moment, and Play moves on with the note saved there', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend()]));
  const { transport, calls } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  f.hooks.review.noteFocus();
  assert.equal(f.$('pat-bulk-help').textContent, BULK_COPY.writing);
  f.$('m-note').value = 'I walk up without';
  windowEnd(f, transport);
  assert.equal(calls.at(-1), 'pause', 'the window still ends');
  assert.equal(f.beat(), 0, 'nothing moves on while writing');
  assert.equal(f.hooks.activeMoment().title, 'Moment 2');
  assert.equal(f.$('pat-bulk-help').textContent, BULK_COPY.paused);
  f.hooks.review.noteBlur();
  assert.equal(f.beat(), 0, 'blur alone does not move on');
  f.hooks.onVideoPlay();
  assert.equal(f.hooks.activeMoment().title, 'Moment 5');
  for (let i = 0; i < 4; i++) await new Promise(resolve => setImmediate(resolve));
  const saved = f.writes.find(w => w.command === 'save_pattern_moment_note');
  assert.equal(saved.args.payload.bookmarkId, 202);
  assert.equal(saved.args.payload.text, 'I walk up without');
});

test('starting to write during the beat stops the advance', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend()]));
  const { transport, calls } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  windowEnd(f, transport);
  f.hooks.review.noteFocus();
  assert.equal(calls.at(-1), 'pause');
  assert.equal(f.beat(), 0);
  assert.equal(f.hooks.review.state.held, true);
});

test('a moment that could not play is not counted as watched', async () => {
  const f = fixture();
  f.hooks.render(snapshot([readyTrend()]));
  const { loads, transport } = withPlay(f);
  await f.click('pat-bulk-start', 'bulk_start');
  loads[0].onError();
  f.beat();
  for (let i = 0; i < 2; i++) { windowEnd(f, transport); f.beat(); }
  assert.equal(f.$('pat-bulk-text').textContent, 'You watched 2 of 3. What keeps repeating?');
});
