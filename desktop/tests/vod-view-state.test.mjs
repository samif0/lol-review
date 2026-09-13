import test from 'node:test';
import assert from 'node:assert/strict';
import { createVodViewRestorer, hasExplicitVodTarget, vodRestorePlan } from '../ui/vod-view-state.mjs';
import { createMatchNavigation, createMatchViewStore } from '../ui/match-navigation.mjs';

const context = { gameId: 42, filePath: 'recording.mp4', mediaDuration: 1800, gameDuration: 1765, objectiveIds: [2, 3] };
const state = {
  schema: 1, gameId: 42, filePath: 'recording.mp4', mediaTime: 12.75, videoWidth: 1920, videoHeight: 1080,
  step: 10, rate: 1.5, muted: true, volume: 0.4, focusedObjectiveId: 3,
  filter: 'clips', zoom: 3, pan: -200,
  clip: { start: 31, end: 76, quality: 'good', note: '  Next fight\nkeep this spacing  ', picker: 'prompt:3:9', userSet: true },
  bookmark: { note: 'unfinished bookmark ', picker: 'obj:2', userSet: true },
  corrections: { schema: 1, gameId: 42, form: { reason: 'unfinished' } },
};

test('same recording resumes exact media time including pre-game footage and preserves raw drafts', () => {
  const plan = vodRestorePlan(state, context);
  assert.equal(plan.mediaTime, 12.75, 'do not collapse loading footage to clamped game time zero');
  assert.deepEqual(plan.videoSize, { width: 1920, height: 1080 });
  assert.equal(plan.focusedObjectiveId, 3);
  assert.equal(plan.step, 10);
  assert.equal(plan.rate, 1.5);
  assert.equal(plan.muted, true);
  assert.equal(plan.volume, 0.4);
  assert.deepEqual(plan.clip, state.clip);
  assert.deepEqual(plan.bookmark, state.bookmark);
  assert.equal(plan.filter, 'clips');
  assert.deepEqual(plan.corrections, state.corrections);
});

test('explicit game time zero or a clip link wins over saved position, focus, filter and pan', () => {
  for (const search of ['?resume=1&t=0', '?resume=1&t=120.5', '?resume=1&clip=17']) {
    const plan = vodRestorePlan(state, { ...context, search });
    assert.equal(plan.explicit, true);
    assert.equal(plan.mediaTime, null);
    assert.equal(plan.focusedObjectiveId, null);
    assert.equal(plan.filter, null);
    assert.equal(plan.zoom, 1);
    assert.equal(plan.pan, 0);
    assert.equal(plan.corrections, null, 'an old selected correction must not replace the explicit moment');
    assert.deepEqual(plan.clip, state.clip, 'unfinished clip work remains available');
  }
  for (const search of ['', '?t=', '?t=%20', '?t=-1', '?t=bad', '?clip=0', '?clip=bad']) assert.equal(hasExplicitVodTarget(search), false);
});

test('other games or schema versions never restore and replaced recordings do not inherit playback positions', () => {
  assert.equal(vodRestorePlan({ ...state, gameId: 43 }, context), null);
  assert.equal(vodRestorePlan({ ...state, schema: 2 }, context), null);
  assert.equal(vodRestorePlan(state, { ...context, gameId: 0 }), null);
  assert.equal(vodRestorePlan(state, { ...context, filePath: 'replacement.mp4' }).mediaTime, null);
  assert.equal(vodRestorePlan(state, { ...context, filePath: 'replacement.mp4' }).videoSize, null);
  assert.equal(vodRestorePlan({ ...state, videoWidth: Infinity }, context).videoSize, null);
  assert.equal(vodRestorePlan({ ...state, filePath: '' }, { ...context, filePath: '' }).mediaTime, null);
  assert.equal(vodRestorePlan(state, { ...context, objectiveIds: [2] }).focusedObjectiveId, null);
});

test('restoration bounds media and game ranges and rejects unsupported transport settings', () => {
  const plan = vodRestorePlan({ ...state, mediaTime: 9999, step: 17, rate: 22, volume: -2,
    zoom: Infinity, pan: 99, clip: { ...state.clip, start: -1, end: 9999, quality: 'invented' } }, context);
  assert.equal(plan.mediaTime, 1800);
  assert.equal(plan.step, 5);
  assert.equal(plan.rate, 1);
  assert.equal(plan.volume, 0);
  assert.equal(plan.zoom, 1);
  assert.equal(plan.pan, 0);
  assert.equal(plan.clip.start, -1);
  assert.equal(plan.clip.end, 1765);
  assert.equal(plan.clip.quality, '');
  assert.equal(vodRestorePlan({ ...state, mediaTime: NaN }, context).mediaTime, null);
});

test('loading VOD can be left without overwriting its unconsumed snapshot, including pagehide', async () => {
  const store = createMatchViewStore();
  const saved = { version: 1, gameId: 42, state, scroll: { top: 600 } };
  store.save(42, 'vod', saved);
  const listeners = new Map();
  const scope = { location: { href: 'revu-app://ui/vodplayer.html?gameId=42&resume=1', search: '?gameId=42&resume=1' },
    addEventListener: (name, fn) => listeners.set(name, fn), scrollTo() {} };
  let providerState = { ...state, mediaTime: 0 }, restoreCount = 0, finishRestore;
  const restoring = new Promise(resolve => { finishRestore = resolve; });
  const readiness = createVodViewRestorer(() => nav.restoreState());
  const nav = createMatchNavigation({ view: 'vod', scope, store,
    document: { getElementById: () => null, querySelectorAll: () => [] },
    canCapture: () => readiness.ready, capture: () => providerState,
    restore: async restored => { restoreCount++; await restoring; providerState = restored; },
  });
  nav.setGame(42);
  listeners.get('pagehide')();
  assert.deepEqual(store.peek(42, 'vod'), saved);
  assert.equal(await nav.navigate('review.html?gameId=42'), true);
  assert.deepEqual(store.peek(42, 'vod'), saved, 'switching does not wait for a stalled recording');
  const completion = readiness.finish();
  assert.equal(readiness.finish(), completion, 'metadata and error finish the same restoration');
  await Promise.resolve();
  listeners.get('pagehide')();
  assert.deepEqual(store.peek(42, 'vod'), saved, 'asynchronous restoration is protected too');
  finishRestore(); await completion;
  assert.equal(restoreCount, 1);
  assert.equal(readiness.ready, true);
  nav.captureState();
  assert.equal(store.peek(42, 'vod').state.mediaTime, 12.75);
});

test('no-media completion enables UI capture without requiring a metadata event', async () => {
  let calls = 0;
  const readiness = createVodViewRestorer(async () => { calls++; return false; });
  assert.equal(readiness.ready, false);
  assert.equal(await readiness.finish(), false);
  assert.equal(readiness.ready, true);
  await readiness.finish();
  assert.equal(calls, 1);
});

test('freshly rendered drafts restore before stalled media and late metadata never overwrites new edits', async () => {
  let draft = '', pendingMediaTime = null, restores = 0;
  const readiness = createVodViewRestorer(() => {
    const plan = vodRestorePlan(state, { ...context, mediaDuration: NaN });
    restores++;
    draft = plan.clip.note;
    pendingMediaTime = plan.mediaTime;
  });
  await readiness.finish(); // Fresh UI rendered; no metadata has arrived.
  assert.equal(readiness.ready, true);
  assert.equal(draft, state.clip.note);
  assert.equal(pendingMediaTime, 12.75);
  draft = 'New note while recording is stalled  ';
  await readiness.finish(); // A late metadata/error callback is harmless.
  assert.equal(draft, 'New note while recording is stalled  ');
  assert.equal(restores, 1);
});
