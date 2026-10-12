import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const source = (await readFile(new URL('../ui/patterns.js', import.meta.url), 'utf8'))
  .replace(/^import .*?;\r?\n/gm, '');

function fixture(reply = async () => ({ ok: true })) {
  const ids = new Map(), writes = [], reads = [], listeners = new Map();
  let serverSnapshot = null;
  const clone = value => JSON.parse(JSON.stringify(value));
  function element() {
    const classes = new Set(), attributes = new Map();
    return {
      children: [], selectors: new Map(), dataset: {}, textContent: '', value: '', hidden: false, disabled: false,
      get firstChild() { return this.children[0] || null; },
      set innerHTML(_) { throw new Error('Pattern content must remain plain text'); },
      style: { setProperty() {} },
      classList: {
        contains: name => classes.has(name),
        add: (...names) => names.forEach(name => classes.add(name)),
        remove: (...names) => names.forEach(name => classes.delete(name)),
        toggle(name, force) {
          const active = force ?? !classes.has(name);
          if (active) classes.add(name); else classes.delete(name);
          return active;
        },
      },
      appendChild(child) { this.children.push(child); child.parent = this; return child; },
      querySelector(selector) { return this.selectors.get(selector) || null; },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute: name => attributes.get(name) ?? null,
      removeAttribute(name) { attributes.delete(name); },
      focus() { this.focused = true; },
      closest() { return null; },
      scrollIntoView() {},
      addEventListener() {},
    };
  }
  const $ = id => { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); };
  $('m-video').readyState = 1;
  const document = {
    readyState: 'loading',
    querySelector: selector => $(selector),
    addEventListener(type, listener) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(listener);
    },
  };
  const context = vm.createContext({ document, window: { addEventListener() {} }, $, setTimeout, clearTimeout,
    console: { error() {} }, matchMedia: () => ({ matches: true }),
    show: (node, on) => { if (node) node.hidden = !on; },
    clear: node => { node.children = []; },
    resolveAssetUrl: (_media, path) => path ? `synthetic-media:${path}` : null,
    tpl(name) {
      const node = element();
      const selectors = name === 'tpl-patcard'
        ? ['.pat-sev', '.pat-card-title', '.pat-card-detail', '.pat-card-sub', '.pat-card-state', '.gamerow-cue']
        : ['.pat-rg', '.pat-rclip', '.pat-rpol', '.pat-rtitle', '.pat-rnote', '.pat-moment-number'];
      for (const selector of selectors) {
        const child = element();
        if (selector === '.gamerow-cue') child.appendChild(element());
        node.selectors.set(selector, child); node.appendChild(child);
      }
      return node;
    },
    readSnapshot: async (command, sample) => {
      reads.push({ command, sample });
      assert.ok(serverSnapshot, 'Only the synthetic snapshot seeded by this test may be fetched');
      return clone(serverSnapshot);
    },
    getInvoke: async () => async (command, args) => {
      writes.push({ command, args: clone(args) });
      const result = await reply(command, args);
      if (result?.ok !== false && command === 'mark_pattern_reviewed') {
        const saved = serverSnapshot.patterns.find(p => p.patternKey === args.payload.patternKey);
        if (saved && !saved.isReviewed) {
          saved.isReviewed = true;
          serverSnapshot.pendingCount -= 1;
          serverSnapshot.reviewedPatternCount += 1;
        }
      }
      return result;
    },
  });
  vm.runInContext(`${source}\nglobalThis.hooks = { render, markReviewed, selectPattern, selectView,
    activePattern, activeMoment, gotoMoment, saveMomentNote, commitPendingNote, setMomentQuality,
    playableSource, playMoment, constrainPlayback, loadPatterns,
    setPlayback: (transport, window) => { _T = transport; _inlineWindow = window; } };`, context);
  const hooks = { ...context.hooks, render(data) {
    serverSnapshot = clone(data);
    return context.hooks.render(data);
  } };
  const cardTitles = () => $('pat-pick').children.map(card => card.querySelector('.pat-card-title').textContent);
  const click = async (id, action) => {
    const target = $(id);
    assert.equal(target.disabled, false, 'The selected control must be enabled');
    target.dataset.action = action;
    for (const listener of listeners.get('click') || []) {
      await listener({ target: { closest: () => target }, preventDefault() {} });
    }
  };
  return { $, hooks, writes, reads, cardTitles, click };
}

function playback(f) {
  const loads = [], calls = [], video = f.$('m-video');
  video.duration = 1200;
  const transport = {
    currentTime: 0, duration: 1200,
    load(url, options) {
      loads.push({ url, ...options });
      video.setAttribute('src', url);
      this.currentTime = options.startSeconds;
    },
    unload() { calls.push('unload'); video.removeAttribute('src'); },
    isExpanded: () => false,
    handleAction: () => false,
    pause() { calls.push('pause'); },
    seekTo(time) { calls.push(time); this.currentTime = time; },
    toggle() { calls.push('toggle'); },
  };
  f.hooks.setPlayback(transport, null);
  return { loads, calls, transport, video };
}

function pattern(key, isReviewed = false, reviewMode = 'trend') {
  return {
    patternKey: key, kind: 'objective', title: `Pattern ${key}`, isReviewed, reviewMode,
    severity: 'medium', moments: [{ evidenceId: `evidence-${key}`, gameId: key, title: `Moment ${key}`,
      bookmarkId: key + 100, sourceKind: 'bookmark', note: '', hasNote: false, polarity: 'bad' }],
  };
}

const snapshot = patterns => ({ patterns, pendingCount: patterns.filter(p => !p.isReviewed && p.reviewMode === 'trend').length,
  reviewedPatternCount: patterns.filter(p => p.isReviewed && p.reviewMode === 'trend').length, emptyText: 'No patterns yet' });

test('reviewed trends remain available after the pending trends in a mixed snapshot', () => {
  const f = fixture();
  f.hooks.render(snapshot([pattern(1, true), pattern(2), pattern(3, true), pattern(4)]));
  assert.deepEqual(f.cardTitles(), ['Pattern 2', 'Pattern 4', 'Pattern 1', 'Pattern 3']);
  assert.equal(f.hooks.activePattern().patternKey, 2);
  assert.equal(f.$('pat-main').hidden, false);
  assert.equal(f.$('pat-review-title').textContent, 'Pattern 2');
  f.hooks.selectPattern(2);
  assert.equal(f.hooks.activePattern().patternKey, 1);
  assert.equal(f.$('pat-markrev').hidden, true);
  assert.match(f.$('pat-pending-text').textContent, /reviewed this trend/);
});

test('finishing a trend keeps its card for revisits and opens the next pending review', async () => {
  const f = fixture(), completed = pattern(1);
  f.hooks.render(snapshot([completed, pattern(2)]));
  await f.hooks.markReviewed();
  assert.deepEqual(f.writes, [{ command: 'mark_pattern_reviewed', args: {
    payload: { patternKey: 1, kind: 'objective', momentCount: 1 },
  } }]);
  assert.deepEqual(f.cardTitles(), ['Pattern 2', 'Pattern 1']);
  assert.equal(f.hooks.activePattern().patternKey, 2);
  assert.equal(f.$('pat-review-title').textContent, 'Pattern 2');
  assert.equal(f.$('pat-markrev').disabled, false, 'The next review remains actionable');
  assert.equal(completed.moments.length, 1, 'Finishing a review does not delete its saved moments');
  assert.equal(f.reads.length, 1, 'A successful finish refreshes the queue and authoritative counts');
});

test('finishing the final trend leaves its moments playable without another finish action', async () => {
  const f = fixture();
  f.hooks.render(snapshot([pattern(1)]));
  await f.hooks.markReviewed();
  assert.deepEqual(f.cardTitles(), ['Pattern 1']);
  assert.equal(f.hooks.activePattern().isReviewed, true);
  assert.equal(f.$('pat-main').hidden, false);
  assert.equal(f.$('pat-empty').hidden, true);
  assert.equal(f.$('pat-markrev').hidden, true);
  await f.hooks.markReviewed();
  assert.equal(f.writes.length, 1, 'Revisiting must not create a duplicate review');
});

test('a failed finish keeps the pattern visible and allows retry', async () => {
  let fail = true;
  const f = fixture(async () => {
    if (fail) throw new Error('Synthetic network failure');
    return { ok: true };
  });
  f.hooks.render(snapshot([pattern(1)]));
  await f.hooks.markReviewed();
  assert.deepEqual(f.cardTitles(), ['Pattern 1']);
  assert.equal(f.hooks.activePattern().isReviewed, false);
  assert.equal(f.$('pat-main').hidden, false);
  assert.equal(f.$('pat-markrev').disabled, false);
  assert.equal(f.$('pat-review-status').hidden, false);
  assert.equal(f.reads.length, 0, 'Failed saves must not refresh away the current review');
  fail = false;
  await f.hooks.markReviewed();
  assert.deepEqual(f.cardTitles(), ['Pattern 1']);
  assert.equal(f.hooks.activePattern().isReviewed, true);
  assert.equal(f.writes.length, 2);
});

test('repeated finish activation writes once while the review is being saved', async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const f = fixture(async () => pending);
  f.hooks.render(snapshot([pattern(1), pattern(2)]));
  const first = f.hooks.markReviewed();
  const duplicate = f.hooks.markReviewed();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.writes.length, 1);
  assert.equal(f.$('pat-markrev').disabled, true);
  assert.equal(f.$('m-note').readOnly, true, 'The finishing note cannot acquire an unsaved edit');
  f.hooks.selectPattern(1);
  assert.equal(f.hooks.activePattern().patternKey, 1, 'The active write keeps its selected pattern');
  release({ ok: true });
  await Promise.all([first, duplicate]);
  assert.deepEqual(f.cardTitles(), ['Pattern 2', 'Pattern 1']);
  assert.equal(f.writes.length, 1, 'The duplicate must not finish the next pattern');
  assert.equal(f.$('m-note').readOnly, false);
});

test('a pending note must save successfully before its trend can be finished', async () => {
  let failNote = true;
  const f = fixture(async command => {
    if (command === 'save_pattern_moment_note' && failNote) throw new Error('Synthetic note write failure');
    return { ok: true };
  });
  f.hooks.render(snapshot([pattern(1)]));
  f.$('m-note').value = 'Remember to check the map before trading.';
  await f.hooks.markReviewed();
  assert.deepEqual(f.writes.map(write => write.command), ['save_pattern_moment_note']);
  assert.deepEqual(f.cardTitles(), ['Pattern 1']);
  assert.equal(f.$('m-note').value, 'Remember to check the map before trading.');
  assert.equal(f.$('pat-markrev').disabled, false);
  failNote = false;
  await f.hooks.markReviewed();
  assert.deepEqual(f.writes.map(write => write.command), [
    'save_pattern_moment_note', 'save_pattern_moment_note', 'mark_pattern_reviewed',
  ]);
  assert.deepEqual(f.cardTitles(), ['Pattern 1']);
  assert.equal(f.hooks.activePattern().isReviewed, true);
});

test('saved moment collections have their own view and never require finishing', async () => {
  const f = fixture();
  f.hooks.render(snapshot([pattern(1), pattern(2, false, 'saved')]));
  assert.deepEqual(f.cardTitles(), ['Pattern 1']);
  f.hooks.selectView('saved');
  assert.deepEqual(f.cardTitles(), ['Pattern 2']);
  assert.equal(f.$('pat-finish').hidden, true);
  assert.equal(f.$('pat-view-saved').getAttribute('aria-pressed'), 'true');
  await f.hooks.markReviewed();
  assert.deepEqual(f.writes, []);
  f.hooks.selectView('trend');
  assert.deepEqual(f.cardTitles(), ['Pattern 1']);
});

test('saved collections open by default when there are no qualifying mistake trends', () => {
  const f = fixture();
  f.hooks.render(snapshot([pattern(2, false, 'saved')]));
  assert.deepEqual(f.cardTitles(), ['Pattern 2']);
  assert.equal(f.$('pat-view-saved').getAttribute('aria-pressed'), 'true');
  f.hooks.selectView('trend');
  assert.equal(f.$('pat-main').hidden, true);
  assert.match(f.$('pat-empty-p').textContent, /at least two games/);
  assert.match(f.$('pat-empty-p').textContent, /Saved moments/);
});

test('unchanged bookmark notes never write or request an exported clip', async () => {
  const f = fixture(), p = pattern(1);
  p.moments[0].note = 'Check the map before pushing.';
  p.moments[0].hasNote = true;
  f.hooks.render(snapshot([p]));
  await f.hooks.commitPendingNote();
  await f.hooks.saveMomentNote(p.moments[0], '  Check the map before pushing.  ');
  assert.deepEqual(f.writes, []);
});

test('editing a bookmark targets its saved record without exporting a clip', async () => {
  const f = fixture(), p = pattern(1);
  p.moments[0].vodPath = 'synthetic-vod.mp4';
  p.moments[0].startTimeSeconds = 105;
  p.moments[0].endTimeSeconds = 135;
  f.hooks.render(snapshot([p]));
  f.$('m-note').value = 'Ward before pushing the next wave.';
  await f.hooks.commitPendingNote();
  assert.equal(f.writes.length, 1);
  assert.equal(f.writes[0].command, 'save_pattern_moment_note');
  assert.equal(f.writes[0].args.payload.bookmarkId, 101);
  assert.equal(f.writes[0].args.payload.autoClip, false);
  assert.equal(f.writes[0].args.payload.text, 'Ward before pushing the next wave.');
  assert.equal(p.moments[0].sourceKind, 'bookmark');
  assert.equal(f.$('m-clipt').hidden, true);
  await f.hooks.commitPendingNote();
  assert.equal(f.writes.length, 1, 'Blur after an accepted save is a no-op');
});

test('edited notes stay synchronized between saved collections and trend revisits', async () => {
  const f = fixture(), trend = pattern(1), saved = pattern(2, false, 'saved');
  saved.moments = [structuredClone(trend.moments[0])];
  f.hooks.render(snapshot([trend, saved]));
  f.$('m-note').value = 'My new takeaway.';
  await f.hooks.commitPendingNote();
  f.hooks.selectView('saved');
  assert.equal(f.$('m-note').value, 'My new takeaway.');
  await f.hooks.commitPendingNote();
  assert.equal(f.writes.length, 1);
});

test('long saved playlists render eight moments at a time and keep the last moments reachable', () => {
  const f = fixture(), p = pattern(1, false, 'saved');
  p.moments = Array.from({ length: 18 }, (_, i) => ({ ...pattern(i + 1).moments[0], title: `Saved ${i + 1}` }));
  f.hooks.render(snapshot([p]));
  assert.equal(f.$('pat-rail').children.length, 8);
  assert.equal(f.$('pat-batch-label').textContent, '1–8 of 18');
  assert.equal(f.$('pat-batch-prev').disabled, true);
  assert.equal(f.$('pat-batch-next').disabled, false);
  f.hooks.gotoMoment(8);
  assert.equal(f.$('pat-batch-label').textContent, '9–16 of 18');
  assert.equal(f.$('pat-rail').children[0].dataset.momIdx, '8');
  f.hooks.gotoMoment(17);
  assert.equal(f.$('pat-rail').children.length, 2);
  assert.equal(f.$('pat-batch-label').textContent, '17–18 of 18');
  assert.equal(f.$('pat-batch-next').disabled, true);
  assert.equal(f.$('m-title').textContent, 'Saved 18');
});

test('bookmark playback stops at its padded end and replay restarts at its padded beginning', () => {
  const f = fixture(), calls = [];
  const transport = { currentTime: 136, pause() { calls.push('pause'); },
    seekTo(time) { calls.push(time); this.currentTime = time; } };
  f.hooks.setPlayback(transport, { start: 105, end: 135 });
  f.hooks.constrainPlayback();
  assert.deepEqual(calls, ['pause', 135]);
  f.hooks.constrainPlayback(true);
  assert.equal(transport.currentTime, 105);
  transport.currentTime = 90;
  f.hooks.constrainPlayback();
  assert.equal(transport.currentTime, 105, 'Seeking cannot escape the preview window');
});

test('fractional recording offsets do not repeatedly seek a rounded playback start', () => {
  const f = fixture(), seeks = [];
  // Chromium reports media time at microsecond precision. With a recording
  // offset of -53.1234567, seeking to game time 300 can report 299.9999993.
  const transport = { currentTime: 299.9999993, duration: 1200,
    pause() {}, seekTo(time) { seeks.push(time); this.currentTime = time - 0.0000007; } };
  f.hooks.setPlayback(transport, { start: 300, end: 330 });
  for (let i = 0; i < 4; i++) f.hooks.constrainPlayback();
  assert.deepEqual(seeks, [], 'A rounded seek result must not start another seek');
  transport.currentTime = 299;
  f.hooks.constrainPlayback();
  assert.deepEqual(seeks, [300], 'A meaningful seek outside the window is still corrected');
  f.hooks.constrainPlayback();
  assert.deepEqual(seeks, [300], 'The corrected seek remains stable after browser rounding');
});

test('Next moment plays consecutive bookmarks in one recording and then a standalone clip', async () => {
  const f = fixture(), p = pattern(1, false, 'saved');
  p.moments = [
    { ...pattern(1).moments[0], title: 'First bookmark', hasVod: true, vodPath: 'same-vod.mp4',
      startTimeSeconds: 105, endTimeSeconds: 135, gameTimeAtVideoStart: -53.1234567 },
    { ...pattern(2).moments[0], title: 'Second bookmark', hasVod: true, vodPath: 'same-vod.mp4',
      startTimeSeconds: 205, endTimeSeconds: 235, gameTimeAtVideoStart: -53.1234567 },
    { ...pattern(3).moments[0], title: 'Saved clip', bookmarkId: null, sourceKind: 'clip',
      hasVod: false, hasClip: true, clipPath: 'kept-clip.mp4', startTimeSeconds: 600 },
  ];
  f.hooks.render(snapshot([p]));
  const { loads, calls, transport } = playback(f);
  f.hooks.playMoment();
  transport.currentTime = 135;
  f.hooks.constrainPlayback();
  assert.ok(calls.includes('pause'), 'The first preview has reached its end');

  await f.click('m-next', 'next_moment');
  assert.equal(f.$('m-title').textContent, 'Second bookmark');
  assert.equal(f.$('m-position').textContent, 'Moment 2 of 3');
  assert.equal(f.$('pat-rail').children[1].getAttribute('aria-pressed'), 'true');
  assert.equal(loads.length, 2);
  assert.equal(loads[1].url, loads[0].url);
  assert.equal(loads[1].startSeconds, 205, 'The second bookmark seeks within the shared recording');
  assert.equal(loads[1].gameTimeAtVideoStart, -53.1234567);
  assert.equal(loads[1].autoplay, true, 'Next plays even after the previous preview reached its end');

  await f.click('m-next', 'next_moment');
  assert.equal(f.$('m-title').textContent, 'Saved clip');
  assert.equal(loads.length, 3);
  assert.equal(loads[2].url, 'synthetic-media:kept-clip.mp4');
  assert.equal(loads[2].startSeconds, 0, 'A standalone clip plays from its own beginning');
  assert.equal(loads[2].autoplay, true);
  assert.equal(f.$('m-next').disabled, true);
  assert.equal(f.$('m-prev').disabled, false);
});

test('Next moment clears the old video when the next saved moment has no playable media', async () => {
  const f = fixture(), p = pattern(1, false, 'saved');
  p.moments = [
    { ...pattern(1).moments[0], hasVod: true, vodPath: 'available-vod.mp4',
      startTimeSeconds: 105, endTimeSeconds: 135 },
    { ...pattern(2).moments[0], title: 'Missing recording', hasVod: false, hasClip: false },
  ];
  f.hooks.render(snapshot([p]));
  const { loads, video } = playback(f);
  f.hooks.playMoment();
  assert.equal(video.hidden, false);
  await f.click('m-next', 'next_moment');
  assert.equal(f.$('m-title').textContent, 'Missing recording');
  assert.equal(loads.length, 1);
  assert.equal(video.getAttribute('src'), null, 'The previous video must not represent the new moment');
  assert.equal(video.hidden, true);
  assert.equal(f.$('m-transport').hidden, true);
  assert.equal(f.$('m-novod').hidden, false);
});

test('advancing moments ignores source-reset events until the new recording has metadata', async () => {
  const f = fixture(), p = pattern(1, false, 'saved');
  p.moments = [
    { ...pattern(1).moments[0], hasVod: true, vodPath: 'previous-vod.mp4',
      startTimeSeconds: 600, endTimeSeconds: 630 },
    { ...pattern(2).moments[0], hasVod: true, vodPath: 'next-vod.mp4',
      startTimeSeconds: 105, endTimeSeconds: 135 },
  ];
  f.hooks.render(snapshot([p]));
  const { calls, transport, video } = playback(f);
  f.hooks.playMoment();
  transport.currentTime = 640;
  transport.load = (url) => {
    // A source transition may still expose the outgoing time while its first
    // events fire. Once reset, HAVE_NOTHING precedes the new metadata event.
    f.hooks.constrainPlayback();
    video.setAttribute('src', url);
    video.readyState = 0;
    transport.currentTime = 0;
    f.hooks.constrainPlayback();
  };
  await f.click('m-next', 'next_moment');
  f.hooks.constrainPlayback();
  assert.deepEqual(calls, [], 'Neither the previous time nor the loading position should trigger a seek');
  video.readyState = 1;
  transport.currentTime = 105;
  f.hooks.constrainPlayback();
  assert.deepEqual(calls, []);
  assert.equal(video.getAttribute('src'), 'synthetic-media:next-vod.mp4');
});

test('a bookmark overlapping a late recording starts at the available recording boundary', () => {
  const f = fixture(), p = pattern(1);
  Object.assign(p.moments[0], { hasVod: true, vodPath: 'late-recording.mp4',
    startTimeSeconds: 105, endTimeSeconds: 135, gameTimeAtVideoStart: 120 });
  f.hooks.render(snapshot([p]));
  const { calls, transport } = playback(f);
  f.hooks.playMoment();
  transport.currentTime = 120;
  f.hooks.constrainPlayback();
  assert.deepEqual(calls, []);
  transport.currentTime = 135;
  f.hooks.constrainPlayback(true);
  assert.deepEqual(calls, [120], 'Replay uses the available start rather than an unreachable earlier time');
});

for (const missingWindow of [
  { name: 'before a late recording begins', origin: 150, duration: 1200 },
  { name: 'after a recording ends', origin: 0, duration: 100 },
]) {
  test(`a bookmark ${missingWindow.name} stops safely without repeated seeks`, () => {
    const f = fixture(), p = pattern(1);
    Object.assign(p.moments[0], { hasVod: true, vodPath: 'short-recording.mp4',
      startTimeSeconds: 105, endTimeSeconds: 135, gameTimeAtVideoStart: missingWindow.origin });
    f.hooks.render(snapshot([p]));
    const { calls, transport, video } = playback(f);
    transport.duration = missingWindow.duration;
    f.hooks.playMoment();
    transport.currentTime = missingWindow.origin || missingWindow.duration;
    transport.unload = () => {
      calls.push('unload');
      // Unloading can emit another media event; it must not recursively seek.
      f.hooks.constrainPlayback();
      video.removeAttribute('src');
    };
    f.hooks.constrainPlayback();
    f.hooks.constrainPlayback();
    assert.deepEqual(calls, ['unload']);
    assert.equal(video.hidden, true);
    assert.equal(video.getAttribute('src'), null);
    assert.match(f.$('m-nstatus').textContent, /outside the available recording/);
  });
}

test('the available recording end accepts rounding and replay returns to the preview start', () => {
  const f = fixture(), calls = [];
  const transport = { currentTime: 119.9999993, duration: 120,
    pause() { calls.push('pause'); }, seekTo(time) { calls.push(time); this.currentTime = time; } };
  f.hooks.setPlayback(transport, { start: 105, end: 135 });
  f.hooks.constrainPlayback();
  assert.deepEqual(calls, ['pause'], 'A rounded end must stop without repeatedly seeking the exact end');
  f.hooks.constrainPlayback(true);
  assert.deepEqual(calls, ['pause', 105]);
});

test('bookmark header names the saved point while the scrub labels show its padded window', () => {
  const f = fixture(), p = pattern(1);
  Object.assign(p.moments[0], { timeLabel: '2:00', championLabel: 'Jinx', startTimeSeconds: 105, endTimeSeconds: 135 });
  f.hooks.render(snapshot([p]));
  assert.equal(f.$('m-glabel').textContent, 'Jinx · 2:00');
  assert.equal(f.$('m-tstart').textContent, '1:45');
  assert.equal(f.$('m-tend').textContent, '2:15');
});

test('a saved clip remains playable when its original recording is unavailable', () => {
  const f = fixture();
  assert.deepEqual(JSON.parse(JSON.stringify(f.hooks.playableSource({
    hasVod: false, hasClip: true, clipPath: 'synthetic-clip.mp4', startTimeSeconds: 600,
  }))), { path: 'synthetic-clip.mp4', startSeconds: 0 });
});

test('rating a bookmark saves explicit quality, preserves selection, and offers a trend refresh', async () => {
  const f = fixture(), p = pattern(1, false, 'saved');
  f.hooks.render(snapshot([p]));
  await f.hooks.setMomentQuality('good');
  assert.deepEqual(f.writes, [{ command: 'set_bookmark_quality', args: {
    payload: { bookmarkId: 101, quality: 'good' },
  } }]);
  assert.equal(f.hooks.activeMoment().bookmarkId, 101);
  assert.equal(f.hooks.activeMoment().polarity, 'good');
  assert.equal(f.$('m-quality-good').getAttribute('aria-pressed'), 'true');
  assert.equal(f.$('pat-refresh-trends').hidden, false);
  assert.equal(f.reads.length, 0, 'A rating must not navigate away from the moment being watched');
  await f.hooks.setMomentQuality('good');
  assert.equal(f.writes.length, 1, 'Clicking the current rating is a no-op');
});

test('rating a clip writes evidence polarity and cannot finish a stale trend', async () => {
  const f = fixture(), p = pattern(1);
  delete p.moments[0].bookmarkId;
  p.moments[0].sourceKind = 'clip';
  f.hooks.render(snapshot([p]));
  await f.hooks.setMomentQuality('neutral');
  assert.deepEqual(f.writes, [{ command: 'set_evidence_polarity', args: {
    payload: { evidenceId: 'evidence-1', polarity: 'neutral' },
  } }]);
  assert.equal(f.$('pat-markrev').disabled, true);
  await f.hooks.markReviewed();
  assert.equal(f.writes.length, 1);
});

test('a failed rating preserves its original value and allows retry', async () => {
  const f = fixture(async () => ({ ok: false, error: 'Synthetic failure' }));
  f.hooks.render(snapshot([pattern(1)]));
  await f.hooks.setMomentQuality('good');
  assert.equal(f.hooks.activeMoment().polarity, 'bad');
  assert.equal(f.$('m-quality-bad').getAttribute('aria-pressed'), 'true');
  assert.equal(f.$('m-quality-good').disabled, false);
  assert.match(f.$('m-nstatus').textContent, /Couldn't save the rating/);
  assert.equal(f.$('pat-refresh-trends').hidden, true);
  assert.equal(f.$('m-note').readOnly, false);
});

test('rating writes lock selection until accepted and do not lose the current note', async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const f = fixture(async command => command === 'set_bookmark_quality' ? pending : { ok: true });
  f.hooks.render(snapshot([pattern(1), pattern(2)]));
  f.$('m-note').value = 'Wait for the next wave.';
  const save = f.hooks.setMomentQuality('good');
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(f.writes.map(w => w.command), ['save_pattern_moment_note', 'set_bookmark_quality']);
  f.hooks.selectPattern(1);
  f.hooks.selectView('saved');
  assert.equal(f.hooks.activePattern().patternKey, 1);
  assert.equal(f.$('m-note').readOnly, true);
  release({ ok: true });
  await save;
  assert.equal(f.$('m-note').value, 'Wait for the next wave.');
  assert.equal(f.$('m-note').readOnly, false);
});

test('Next clicked during a rating save opens and plays the requested bookmark after saving', async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const f = fixture(async command => command === 'set_bookmark_quality' ? pending : { ok: true });
  const p = pattern(1, false, 'saved');
  p.moments = [
    { ...pattern(1).moments[0], hasVod: true, vodPath: 'review-vod.mp4',
      startTimeSeconds: 105, endTimeSeconds: 135 },
    { ...pattern(2).moments[0], title: 'Next bookmark', hasVod: true, vodPath: 'review-vod.mp4',
      startTimeSeconds: 205, endTimeSeconds: 235 },
  ];
  f.hooks.render(snapshot([p]));
  const { loads } = playback(f);
  f.hooks.playMoment();
  const save = f.hooks.setMomentQuality('good');
  await new Promise(resolve => setImmediate(resolve));
  await f.click('m-next', 'next_moment');
  assert.equal(f.hooks.activeMoment().bookmarkId, 101, 'Selection stays with the moment being saved');
  assert.equal(loads.length, 1);
  release({ ok: true });
  await save;
  assert.equal(p.moments[0].polarity, 'good');
  assert.equal(f.hooks.activeMoment().bookmarkId, 102, 'The click is honored after the rating is accepted');
  assert.equal(f.$('m-title').textContent, 'Next bookmark');
  assert.equal(loads.length, 2);
  assert.equal(loads[1].startSeconds, 205);
  assert.equal(loads[1].autoplay, true);
  assert.equal(f.$('m-note').readOnly, false);
});

test('a failed rating keeps its error visible and discards a waiting Next request', async () => {
  let release, ratingAttempt = 0;
  const pending = new Promise(resolve => { release = resolve; });
  const f = fixture(async command => command === 'set_bookmark_quality' && ratingAttempt++ === 0
    ? pending : { ok: true });
  const p = pattern(1, false, 'saved');
  p.moments = [
    { ...pattern(1).moments[0], hasVod: true, vodPath: 'review-vod.mp4',
      startTimeSeconds: 105, endTimeSeconds: 135 },
    { ...pattern(2).moments[0], hasVod: true, vodPath: 'review-vod.mp4',
      startTimeSeconds: 205, endTimeSeconds: 235 },
  ];
  f.hooks.render(snapshot([p]));
  const { loads } = playback(f);
  f.hooks.playMoment();
  const save = f.hooks.setMomentQuality('good');
  await new Promise(resolve => setImmediate(resolve));
  await f.click('m-next', 'next_moment');
  release({ ok: false, error: 'Synthetic rating failure' });
  await save;
  assert.equal(f.hooks.activeMoment().bookmarkId, 101);
  assert.equal(f.hooks.activeMoment().polarity, 'bad');
  assert.equal(loads.length, 1);
  assert.match(f.$('m-nstatus').textContent, /Couldn't save the rating/);
  assert.equal(f.$('m-note').readOnly, false);

  await f.hooks.setMomentQuality('good');
  assert.equal(f.hooks.activeMoment().polarity, 'good');
  assert.equal(f.hooks.activeMoment().bookmarkId, 101, 'A later retry must not reuse the failed save\'s navigation');
  assert.equal(loads.length, 1);
  await f.click('m-next', 'next_moment');
  assert.equal(f.hooks.activeMoment().bookmarkId, 102, 'A fresh Next click still works');
  assert.equal(loads.length, 2);
});
