import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const html = readFileSync(new URL('../clip.html', import.meta.url), 'utf8');
const script = html.match(/<script>\s*([\s\S]*?)<\/script>/)[1];

const SEGMENTS = [
  { start: 1.2, end: 3.5, text: 'first gank comes at three minutes' },
  { start: 4, end: 7.25, text: 'ward the river bush' },
  { start: 65.5, end: 68, text: 'back on the cannon wave' },
];
const TRANSCRIPT_META = {
  title: 'Narrated clip',
  duration_s: 30,
  narrated: true,
  has_transcript: true,
  transcript_language: 'en',
};
const LIST_TOP = 200;
const ROW_HEIGHT = 48;
const LIST_HEIGHT = 120;

// A small DOM element stand-in. Listeners are kept per type so several
// handlers can coexist, and any innerHTML write is recorded as a failure.
function makeElement(tag, { hidden = false, classes = [], log } = {}) {
  const classSet = new Set(classes);
  const attrs = new Map();
  const listeners = new Map();
  const element = {
    tagName: tag.toUpperCase(),
    hidden,
    disabled: false,
    textContent: '',
    value: '',
    type: '',
    style: {},
    children: [],
    scrollTop: 0,
    offsetTop: 0,
    clientHeight: 0,
    listeners,
    classList: {
      add: (...values) => values.forEach(value => classSet.add(value)),
      remove: (...values) => values.forEach(value => classSet.delete(value)),
      contains: value => classSet.has(value),
    },
    get className() { return [...classSet].join(' '); },
    set className(value) {
      classSet.clear();
      String(value).split(/\s+/).filter(Boolean).forEach(name => classSet.add(name));
    },
    set innerHTML(value) { log.innerHTML.push(value); },
    get innerHTML() { return ''; },
    setAttribute: (key, value) => attrs.set(key, String(value)),
    removeAttribute: key => attrs.delete(key),
    getAttribute: key => (attrs.has(key) ? attrs.get(key) : null),
    appendChild(child) {
      this.children.push(child);
      if (typeof this.onAppend === 'function') this.onAppend(child);
      return child;
    },
    addEventListener(type, callback) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(callback);
    },
    dispatch(type, event = {}) {
      return Promise.all((listeners.get(type) || []).map(callback => callback({
        preventDefault() {},
        target: element,
        ...event,
      })));
    },
    focus() {},
    select() {},
    querySelector: () => null,
  };
  return element;
}

function page(meta, options = {}) {
  const log = { innerHTML: [], cues: [], tracks: [], requests: [], writes: [] };
  const elements = new Map();
  for (const match of html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
    const [, tag, attributes, id] = match;
    const classes = (attributes.match(/class="([^"]*)"/)?.[1] || '').split(/\s+/).filter(Boolean);
    elements.set(id, makeElement(tag, { hidden: /\bhidden\b/.test(attributes), classes, log }));
  }

  const list = elements.get('transcript-list');
  list.offsetTop = LIST_TOP;
  list.clientHeight = LIST_HEIGHT;
  // Rows are laid out top to bottom inside the list as they are appended.
  list.onAppend = item => {
    const index = list.children.length - 1;
    for (const child of item.children) child.offsetTop = LIST_TOP + index * ROW_HEIGHT;
  };

  const video = elements.get('clip-video');
  Object.assign(video, { paused: true, muted: false, duration: NaN, currentTime: 0, playbackRate: 1 });
  video.play = () => { video.paused = false; return Promise.resolve(); };
  video.pause = () => { video.paused = true; };
  if (options.textTracks !== false) {
    video.addTextTrack = (kind, label, language) => {
      const track = { kind, label, language, mode: 'hidden', cues: [] };
      track.addCue = cue => { track.cues.push(cue); log.cues.push(cue); };
      log.tracks.push(track);
      return track;
    };
  }

  const document = makeElement('document', { log });
  document.getElementById = id => elements.get(id) || null;
  document.querySelector = () => makeElement('div', { log });
  document.createElement = tag => makeElement(tag, { log });

  const transcriptResponse = options.transcriptResponse || (() => Promise.resolve({
    ok: true,
    json: async () => ({ id: 'Abc1234', language: 'en', segments: SEGMENTS }),
  }));

  const context = {
    document,
    navigator: { clipboard: { writeText: async text => { log.writes.push(text); } } },
    location: { search: '?id=Abc1234', pathname: '/clip' },
    URLSearchParams,
    innerWidth: 1440,
    fetch: (url, init) => {
      log.requests.push({ url, method: init?.method || 'GET' });
      if (url.includes('/clip-meta/')) return Promise.resolve({ ok: true, json: async () => meta });
      if (url.includes('/clip-transcript/')) return transcriptResponse();
      return Promise.resolve({ ok: true, json: async () => ({}) });
    },
    setTimeout: () => 0,
    clearTimeout: () => {},
  };
  if (options.innerWidth !== undefined) context.innerWidth = options.innerWidth;
  if (options.intersectionObserver) {
    context.IntersectionObserver = class IntersectionObserver {
      constructor(callback) {
        log.observers = log.observers || [];
        log.observers.push(this);
        this.callback = callback;
        this.targets = [];
      }
      observe(target) { this.targets.push(target); }
      report(intersectionRatio) {
        this.callback(this.targets.map(target => ({ target, intersectionRatio })));
      }
    };
  }
  if (options.textTracks !== false) {
    context.VTTCue = class VTTCue {
      constructor(startTime, endTime, text) {
        Object.assign(this, { startTime, endTime, text, line: 'auto', size: 100 });
      }
    };
  }
  vm.runInNewContext(script, context);

  return {
    log,
    video,
    list,
    document,
    el: id => elements.get(id),
    rows: () => list.children.map(item => item.children[0]),
    settle: async () => {
      for (let i = 0; i < 5; i++) await new Promise(resolve => setImmediate(resolve));
    },
  };
}

test('A transcript renders one button row per segment with time and text', async () => {
  const clip = page(TRANSCRIPT_META);
  await clip.settle();
  assert.equal(clip.el('clip-transcript').hidden, false);
  assert.equal(clip.el('transcript-status').textContent, 'Auto-generated transcript');
  assert.equal(clip.el('transcript-copy').hidden, false);
  assert.equal(clip.el('clip-narrated').hidden, false);

  const rows = clip.rows();
  assert.equal(rows.length, 3);
  assert.deepEqual(rows.map(row => row.children[0].textContent), ['0:01', '0:04', '1:05']);
  assert.deepEqual(rows.map(row => row.children[1].textContent), SEGMENTS.map(seg => seg.text));
  assert.deepEqual(rows.map(row => row.className), ['clip-cue', 'clip-cue', 'clip-cue']);
  assert.deepEqual(rows.map(row => row.getAttribute('data-i')), ['0', '1', '2']);
  assert.deepEqual(rows.map(row => row.children.map(span => span.className)),
    Array(3).fill(['clip-cue-time', 'clip-cue-text']));
  assert.equal(rows[0].type, 'button');
  assert.deepEqual(clip.log.innerHTML, [], 'Transcript text must never be written as HTML');
  assert.deepEqual(clip.log.requests.map(request => request.url), [
    'https://clips.revu.lol/clip-meta/Abc1234',
    'https://clips.revu.lol/clip-transcript/Abc1234',
    'https://clips.revu.lol/clip-view/Abc1234',
  ]);
});

test('Captions become a showing track whose cues sit above the game HUD', async () => {
  const clip = page(TRANSCRIPT_META);
  await clip.settle();
  assert.equal(clip.log.tracks.length, 1);
  const [track] = clip.log.tracks;
  assert.deepEqual([track.kind, track.label, track.language, track.mode], ['captions', 'Transcript', 'en', 'showing']);
  assert.equal(clip.log.cues.length, 3);
  assert.ok(clip.log.cues.every(cue => cue.line === -4 && cue.size === 80));
  assert.deepEqual(clip.log.cues.map(cue => [cue.startTime, cue.endTime, cue.text]),
    SEGMENTS.map(seg => [seg.start, seg.end, seg.text]));
});

test('Clicking a row seeks the video to that line', async () => {
  const clip = page(TRANSCRIPT_META);
  await clip.settle();
  await clip.rows()[1].dispatch('click');
  assert.ok(Math.abs(clip.video.currentTime - SEGMENTS[1].start) < 0.05);
  assert.equal(clip.el('clip-current-time').textContent, '0:04');
});

test('The CC button and the c key toggle the caption track', async () => {
  const clip = page(TRANSCRIPT_META);
  await clip.settle();
  const cc = clip.el('clip-cc');
  const [track] = clip.log.tracks;
  assert.equal(cc.hidden, false);
  assert.equal(cc.getAttribute('aria-pressed'), 'true');

  await cc.dispatch('click');
  assert.equal(track.mode, 'hidden');
  assert.equal(cc.getAttribute('aria-pressed'), 'false');
  await cc.dispatch('click');
  assert.equal(track.mode, 'showing');
  assert.equal(cc.getAttribute('aria-pressed'), 'true');

  await clip.document.dispatch('keydown', { key: 'c', target: { tagName: 'BODY' } });
  assert.equal(track.mode, 'hidden');
  assert.equal(cc.getAttribute('aria-pressed'), 'false');
});

test('timeupdate marks the current line and scrolls only the list', async () => {
  const clip = page(TRANSCRIPT_META);
  await clip.settle();
  const rows = clip.rows();

  clip.video.currentTime = 5;
  await clip.video.dispatch('timeupdate');
  assert.deepEqual(rows.map(row => row.getAttribute('aria-current')), [null, 'true', null]);
  assert.equal(rows[1].classList.contains('is-active'), true);
  const expected = rows[1].offsetTop - LIST_TOP - LIST_HEIGHT / 3;
  assert.equal(clip.list.scrollTop, expected);
  assert.notEqual(clip.list.scrollTop, 0);

  clip.video.currentTime = 66;
  await clip.video.dispatch('timeupdate');
  assert.deepEqual(rows.map(row => row.getAttribute('aria-current')), [null, null, 'true']);
  assert.equal(rows[1].classList.contains('is-active'), false);
  assert.equal(clip.list.scrollTop, rows[2].offsetTop - LIST_TOP - LIST_HEIGHT / 3);
});

test('Recent list interaction pauses auto-follow', async () => {
  const clip = page(TRANSCRIPT_META);
  await clip.settle();
  await clip.list.dispatch('wheel');
  clip.list.scrollTop = 3;
  clip.video.currentTime = 66;
  await clip.video.dispatch('timeupdate');
  assert.equal(clip.rows()[2].getAttribute('aria-current'), 'true');
  assert.equal(clip.list.scrollTop, 3, 'A viewer reading the list keeps their scroll position');
});

test('Copy transcript writes timestamped lines', async () => {
  const clip = page(TRANSCRIPT_META);
  await clip.settle();
  await clip.el('transcript-copy').dispatch('click');
  assert.deepEqual(clip.log.writes, [
    '0:01 first gank comes at three minutes\n0:04 ward the river bush\n1:05 back on the cannon wave',
  ]);
  assert.equal(clip.el('transcript-copy').textContent, 'Copied');
});

test('Clips without a transcript make no transcript request', async () => {
  const clip = page({ title: 'Plain clip', duration_s: 30, narrated: false, has_transcript: false });
  await clip.settle();
  assert.deepEqual(clip.log.requests, [
    { url: 'https://clips.revu.lol/clip-meta/Abc1234', method: 'GET' },
    { url: 'https://clips.revu.lol/clip-view/Abc1234', method: 'POST' },
  ]);
  assert.equal(clip.el('clip-transcript').hidden, true);
  assert.equal(clip.el('clip-cc').hidden, true);
  assert.equal(clip.el('clip-narrated').hidden, true);
  assert.equal(clip.log.tracks.length, 0);
});

for (const [name, transcriptResponse] of [
  ['a 404', () => Promise.resolve({ ok: false, json: async () => ({ error: 'not_found' }) })],
  ['a network error', () => Promise.reject(new Error('offline'))],
  ['a malformed document', () => Promise.resolve({
    ok: true, json: async () => ({ id: 'Abc1234', segments: [{ start: '1', end: 2, text: 'x' }] }),
  })],
]) {
  test(`Transcript fetch failure (${name}) shows Transcript unavailable.`, async () => {
    const clip = page(TRANSCRIPT_META, { transcriptResponse });
    await clip.settle();
    assert.equal(clip.el('clip-transcript').hidden, false);
    assert.equal(clip.el('transcript-status').textContent, 'Transcript unavailable.');
    assert.equal(clip.el('transcript-copy').hidden, true);
    assert.equal(clip.rows().length, 0);
    assert.equal(clip.el('clip-cc').hidden, true);
    assert.equal(clip.el('state-clip').hidden, false, 'The clip still plays without a transcript');
  });
}

test('Browsers without caption support still get the transcript panel', async () => {
  const clip = page(TRANSCRIPT_META, { textTracks: false });
  await clip.settle();
  assert.equal(clip.rows().length, 3);
  assert.equal(clip.el('clip-cc').hidden, true);
  await clip.document.dispatch('keydown', { key: 'c', target: { tagName: 'BODY' } });
  assert.equal(clip.log.tracks.length, 0);
});

test('Auto-follow uses IntersectionObserver visibility when it is available', async () => {
  const clip = page(TRANSCRIPT_META, { intersectionObserver: true, innerWidth: 375 });
  await clip.settle();
  const [observer] = clip.log.observers;
  assert.deepEqual(observer.targets, [clip.list]);
  const rows = clip.rows();

  observer.report(0);
  clip.video.currentTime = 5;
  await clip.video.dispatch('timeupdate');
  assert.equal(rows[1].getAttribute('aria-current'), 'true');
  assert.equal(clip.list.scrollTop, 0, 'An off-screen list is never scrolled');

  observer.report(0.4);
  clip.video.currentTime = 66;
  await clip.video.dispatch('timeupdate');
  assert.equal(clip.list.scrollTop, rows[2].offsetTop - LIST_TOP - LIST_HEIGHT / 3);
});

test('Without IntersectionObserver, narrow screens do not auto-follow', async () => {
  const clip = page(TRANSCRIPT_META, { innerWidth: 375 });
  await clip.settle();
  clip.video.currentTime = 66;
  await clip.video.dispatch('timeupdate');
  assert.equal(clip.rows()[2].getAttribute('aria-current'), 'true');
  assert.equal(clip.list.scrollTop, 0);
});

test('The proxy response shape with no language falls back to English captions', async () => {
  // GET /clip-transcript/:id answers { id, language, segments } and stores a
  // missing language as ''; clip-meta then reports transcript_language null.
  const clip = page({ ...TRANSCRIPT_META, transcript_language: null }, {
    transcriptResponse: () => Promise.resolve({
      ok: true,
      json: async () => ({ id: 'Abc1234', language: '', segments: SEGMENTS }),
    }),
  });
  await clip.settle();
  assert.equal(clip.log.tracks.length, 1);
  assert.equal(clip.log.tracks[0].language, 'en');
  assert.equal(clip.rows().length, 3);
});

test('A valid transcript with no segments says no speech was detected', async () => {
  const clip = page(TRANSCRIPT_META, {
    transcriptResponse: () => Promise.resolve({
      ok: true,
      json: async () => ({ id: 'Abc1234', language: 'en', segments: [] }),
    }),
  });
  await clip.settle();
  assert.equal(clip.el('clip-transcript').hidden, false);
  assert.equal(clip.el('transcript-status').textContent, 'No speech detected.');
  assert.equal(clip.el('transcript-copy').hidden, true);
  assert.equal(clip.el('clip-cc').hidden, true);
  assert.equal(clip.rows().length, 0);
});

test('Space on a focused button activates it instead of toggling playback', async () => {
  const clip = page(TRANSCRIPT_META);
  await clip.settle();
  const prevented = [];
  const press = target => clip.document.dispatch('keydown', {
    key: ' ',
    target,
    preventDefault() { prevented.push(target.tagName); },
  });
  const button = { tagName: 'BUTTON', closest: selector => (selector.includes('button') ? button : null) };
  await press(button);
  assert.equal(clip.video.paused, true, 'Space on a transcript row, CC or Copy must not play the video');
  assert.deepEqual(prevented, [], 'The native button activation must not be cancelled');

  await press({ tagName: 'BODY', closest: () => null });
  assert.equal(clip.video.paused, false, 'Space elsewhere still toggles playback');
});
