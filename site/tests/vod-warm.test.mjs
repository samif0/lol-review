// Read-ahead for a shared full game (vod-warm.js): which segments a span of the game needs, what
// is wanted first, the fetch queue (lanes, a slow line, refusals, the rate cap) and the page
// around it: nothing before play, the seconds around the playhead, the second under the
// pointer, the game's moments once the buffer is full, and when it must stay quiet.
import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { finishedMeta, flush, fragments, hlsMeta, openPage, segmentUrl, sources, token } from './vod-harness.mjs';

const META = 'GET /vod-meta/Abc1234';
const BASE = `https://clips.revu.lol/vod-hls/Abc1234/${token(1)}/`;
const range = (from, to) => Array.from({ length: Math.abs(to - from) + 1 }, (unused, i) => (to >= from ? from + i : from - i));
const plain = value => JSON.parse(JSON.stringify(value));

function pure() {
  const context = {};
  vm.createContext(context);
  vm.runInContext(sources.warm, context);
  return context.RevuVodWarm;
}

// The queue alone: a clock, timers, a video with a buffer and a fetch the test answers by hand.
function bench({ answer = null } = {}) {
  let now = 5_000_000;
  const timers = [], calls = [];
  let ranges = [];
  const video = { currentTime: 0, buffered: { get length() { return ranges.length; }, start: i => ranges[i][0], end: i => ranges[i][1] } };
  const context = {
    Date: class extends Date { static now() { return now; } },
    setTimeout: (fn, ms) => { timers.push({ fn, at: now + ms, live: true }); return timers.length; },
    clearTimeout: handle => { if (timers[handle - 1]) timers[handle - 1].live = false; },
    AbortController,
  };
  vm.createContext(context);
  vm.runInContext(sources.warm, context);
  const fetch = (url, init) => new Promise((resolve, reject) => {
    calls.push({ url, init, resolve, reject });
    if (answer) resolve(answer());
  });
  const queue = context.RevuVodWarm.queue({ root: context, video, fetch });
  return {
    W: context.RevuVodWarm, queue, calls, video,
    seqs: () => calls.map(c => Number(c.url.match(/(\d+)\.m4s$/)[1])),
    buffer(list) { ranges = list; },
    entries: (list, idle = false) => list.map(seq => ({ url: segmentUrl(1, seq), start: seq * 2, end: seq * 2 + 2, idle })),
    pending: () => timers.filter(t => t.live).map(t => t.at - now),
    // Moves the clock on, running each timer that comes due.
    async advance(ms) {
      const end = now + ms;
      await flush();
      for (;;) {
        const due = timers.filter(t => t.live && t.at <= end).sort((a, b) => a.at - b.at)[0];
        if (!due) break;
        now = due.at;
        due.live = false;
        due.fn();
        await flush();
      }
      now = end;
      await flush();
    },
    tick(ms) { now += ms; },
  };
}

const ok = (bytes = 0) => ({ ok: true, status: 200, arrayBuffer: async () => new ArrayBuffer(bytes) });
const SEGMENT_BYTES = 1_300_000;

test('the segments that hold a span of the game, nearest its start first, and only this share\'s own', () => {
  const W = pure();
  const list = fragments(10);
  const seqs = (from, to, frags = list, base = BASE) => plain(W.segmentsBetween(frags, base, from, to)).map(s => Number(s.url.match(/(\d+)\.m4s$/)[1]));
  assert.deepEqual(seqs(3, 3), [1], 'a moment is in one segment');
  assert.deepEqual(seqs(4, 4), [2], 'a segment starts on its first second');
  assert.deepEqual(seqs(3.5, 4.5), [1, 2], 'a jump near a segment\'s end needs the next one too');
  assert.deepEqual(seqs(4, 12), [2, 3, 4, 5]);
  assert.deepEqual(seqs(12, 4), [5, 4, 3, 2], 'backwards: nearest first');
  assert.deepEqual(seqs(4, -10), [1, 0], 'before the start there is nothing');
  assert.deepEqual(seqs(18, 40), [9], 'nor past the last uploaded segment');
  assert.deepEqual(seqs(20, 30), []);
  assert.deepEqual(plain(W.segmentsBetween(list, BASE, 3, 3)), [{ url: segmentUrl(1, 1), start: 2, end: 4 }]);
  assert.deepEqual([seqs(0, 4, null), seqs(0, 4, []), seqs(0, 4, list, ''), seqs(NaN, 4), seqs(0, Infinity)], [[], [], [], [], []]);

  // Nothing is fetched on a playlist's word: another host, another share, another kind of file.
  const odd = [
    { start: 0, duration: 2, url: 'https://evil.example/vod-hls/Abc1234/x/0.m4s' },
    { start: 2, duration: 2, url: `${BASE}../Zzz9999/1.m4s` },
    { start: 4, duration: 2, url: `${BASE}index.m3u8` },
    { start: 6, duration: 2, url: `${BASE}3.m4s?x=1` },
    { start: 8, duration: 2, url: `${BASE}4.m4s` },
    { start: 10, duration: 2 },
  ];
  assert.deepEqual(plain(W.segmentsBetween(odd, BASE, 0, 12)).map(s => s.url), [`${BASE}4.m4s`]);
});

test('what is wanted, most wanted first: the pointer, ahead, just behind, the rest ahead, then the moments', () => {
  const W = pure();
  const around = p => [{ from: p, to: p + 12 }, { from: p, to: p - 10 }, { from: p + 30, to: p + 31 }, { from: p + 12, to: p + 30 }, { from: p - 10, to: p - 20 }];
  assert.deepEqual(plain(W.spans(600, null, [])), around(600), 'the next skips forward, a skip back, the 30 s skip, the rest ahead, a second skip back');
  assert.deepEqual(plain(W.spans(600, 250, [30, 920, 610, 400])), [
    { from: 250, to: 251 }, ...around(600),
    { from: 610, to: 611, idle: true }, { from: 400, to: 401, idle: true }, { from: 920, to: 921, idle: true }, { from: 30, to: 31, idle: true },
  ], 'the moments wait for a full buffer, nearest the playhead first');
  assert.equal(W.spans(0, NaN, range(1, 200)).filter(s => s.idle).length, 60, 'the 60 nearest moments');
  assert.deepEqual(plain(W.spans(undefined, undefined, null)), around(0));
  assert.deepEqual([W.WARM.AGAIN_MS, W.WARM.PER_10S, W.WARM.LANES, W.WARM.LANES_MAX], [50 * 60000, 80, 2, 6],
    'a browser keeps a segment for an hour; the proxy allows an address 200 media requests per 10 s');
});

test('the queue fetches two side by side at first, more as the bytes come fast, in the order asked', async () => {
  const run = bench();
  run.queue.set(run.entries(range(10, 19)));
  assert.deepEqual(run.seqs(), [10, 11]);
  assert.equal(run.calls[0].init.priority, 'low', 'behind the segment hls.js is fetching');
  assert.equal(run.calls[0].init.signal.aborted, false);
  assert.deepEqual(Object.keys(run.calls[0].init).sort(), ['priority', 'signal'], 'no headers: no preflight');

  run.calls[0].resolve(ok(SEGMENT_BYTES));
  await flush();
  assert.deepEqual(run.seqs(), [10, 11, 12, 13], 'fast bytes: a third lane');
  for (let i = 1; i < 10; i += 1) { run.calls[i].resolve(ok(SEGMENT_BYTES)); await flush(); }
  assert.deepEqual(run.seqs(), range(10, 19));
  assert.deepEqual(run.pending(), [], 'nothing left: no timer');

  // Asked again (the viewer plays on): what was fetched, sits in the buffer, hls.js is fetching
  // now or fetched itself is left alone.
  run.buffer([[60, 64]]);
  run.queue.fetched(segmentUrl(1, 33));
  run.queue.set([...run.entries([12, 19, 30, 30, 31, 32, 33, 34])], segmentUrl(1, 32));
  assert.deepEqual(run.seqs().slice(10), [34], 'two were fetched, two are buffered, one hls.js is fetching, one it fetched');
  run.calls[10].resolve(ok(SEGMENT_BYTES));
  await flush();

  // Fifty minutes on, the browser's copy is about to expire: once more.
  run.tick(50 * 60000);
  run.queue.set(run.entries([12, 33]));
  assert.deepEqual(run.seqs().slice(11), [12, 33]);
});

test('a new ask replaces the queue; the moments wait for a full buffer and take three lanes at most', async () => {
  const run = bench();
  run.queue.set(run.entries(range(100, 105)));
  run.queue.set([...run.entries([200, 201], true), ...run.entries([300])]);
  assert.deepEqual(run.seqs(), [100, 101], 'both lanes are busy with the first ask');
  run.calls[0].resolve(ok());
  run.calls[1].resolve(ok());
  await flush();
  assert.deepEqual(run.seqs(), [100, 101, 300], 'what the viewer needs now goes before the moments, and 102 to 105 are forgotten');
  run.calls[2].resolve(ok());
  await flush();
  assert.deepEqual(run.pending(), [1000], 'the buffer is not full: look again in a second');
  await run.advance(3000);
  assert.deepEqual(run.seqs(), [100, 101, 300]);

  run.video.currentTime = 50;
  run.buffer([[40, 64.9]]);
  await run.advance(1000);
  assert.deepEqual(run.seqs(), [100, 101, 300], '14.9 s ahead is not full');
  run.buffer([[40, 65]]);
  await run.advance(1000);
  assert.deepEqual(run.seqs(), [100, 101, 300, 200, 201]);

  const many = bench({ answer: () => ok(SEGMENT_BYTES) });
  many.buffer([[0, 30]]);
  many.queue.set(many.entries(range(100, 103)));
  await flush();
  assert.equal(many.calls.length, 4);
  many.queue.set(many.entries(range(500, 520), true));
  assert.equal(many.calls.length - 4, 3, 'six lanes by now, yet three for the moments');
});

test('a slow line gets fewer lanes, then none for a while', async () => {
  const run = bench();
  const slow = () => { let done; const body = new Promise(resolve => { done = resolve; }); return { res: { ok: true, arrayBuffer: () => body }, done: () => done(new ArrayBuffer(SEGMENT_BYTES)) }; };
  run.queue.set(run.entries(range(1, 9)));
  const first = slow(), second = slow();
  run.calls[0].resolve(first.res);
  run.calls[1].resolve(second.res);
  await flush();
  run.tick(1500); // the bytes of both took a second and a half
  first.done();
  await flush();
  assert.deepEqual(run.seqs(), [1, 2], 'one lane now, and it is busy');
  second.done();
  await flush();
  assert.deepEqual(run.seqs(), [1, 2], 'no lane left: the segment that plays next comes first');
  assert.deepEqual(run.pending(), [20000]);
  await run.advance(19999);
  assert.deepEqual(run.seqs(), [1, 2]);
  await run.advance(1);
  assert.deepEqual(run.seqs(), [1, 2, 3], 'one lane again after 20 s');
});

test('a refusal or a failure stops read-ahead for 20 s and is never an error on the page', async () => {
  for (const settle of [call => call.resolve({ ok: false, status: 403 }), call => call.reject(new TypeError('network')), call => call.resolve(null)]) {
    const run = bench();
    run.queue.set(run.entries(range(1, 6)));
    settle(run.calls[0]);
    run.calls[1].resolve(ok());
    await flush();
    assert.deepEqual(run.seqs(), [1, 2], 'the rest is dropped');
    run.queue.set(run.entries(range(1, 6)));
    assert.deepEqual(run.seqs(), [1, 2], 'and nothing new for a while');
    await run.advance(20000);
    assert.deepEqual(run.seqs(), [1, 2, 1, 3], 'then again, the one that failed first');
  }
});

test('never more than 80 fetches in 10 seconds, whatever is asked', async () => {
  const run = bench({ answer: () => ok(SEGMENT_BYTES) });
  run.queue.set(run.entries(range(0, 299)));
  await flush(40);
  assert.equal(run.calls.length, 80);
  await run.advance(9999);
  assert.equal(run.calls.length, 80);
  await run.advance(1);
  await flush(40);
  assert.equal(run.calls.length, 160);
});

test('stop forgets the queue, a new token forgets what was fetched, destroy cancels what is in flight', async () => {
  const run = bench();
  run.queue.set(run.entries(range(1, 6)));
  run.queue.stop();
  run.calls[0].resolve(ok());
  run.calls[1].resolve(ok());
  await flush();
  assert.deepEqual(run.seqs(), [1, 2]);
  run.queue.set(run.entries([1, 2, 3]));
  assert.deepEqual(run.seqs(), [1, 2, 3], 'loading runs again');
  run.queue.reset();
  run.queue.set(run.entries([1]));
  assert.deepEqual(run.seqs(), [1, 2, 3, 1]);
  run.queue.destroy();
  assert.equal(run.calls[3].init.signal.aborted, true);
  run.queue.set(run.entries([7, 8]));
  await run.advance(60000);
  assert.deepEqual(run.seqs(), [1, 2, 3, 1]);
});

// The page.

const segments = run => run.requests.filter(r => /\.m4s$/.test(r.path)).map(r => Number(r.path.match(/(\d+)\.m4s$/)[1]));

async function playing(options = {}) {
  const run = openPage({ routes: { [META]: finishedMeta(), ...(options.routes || {}) }, globals: options.globals });
  await run.advance(0);
  run.$('vod-play').click();
  await run.advance(0);
  return run;
}

test('nothing is fetched ahead before play or before hls.js lists the segments; then the seconds ahead, then the moments', async () => {
  const run = openPage({ routes: { [META]: finishedMeta() } });
  await run.advance(0);
  run.video.dispatch('playing');
  run.video.dispatch('seeking');
  run.playTo(5);
  await run.advance(5000);
  assert.deepEqual(run.requests.map(r => `${r.method} ${r.path}`), [META], 'an open page fetches nothing of the game');

  run.video.currentTime = 0;
  run.$('vod-play').click();
  await run.advance(0);
  const hls = run.hls();
  run.video.dispatch('playing');
  run.video.dispatch('seeking');
  await run.advance(1000);
  assert.deepEqual(segments(run), [], 'no segment list yet');
  assert.deepEqual(run.pendingPolls(), [], 'and no timer of its own');

  hls.level(1000, false, fragments(500));
  run.video.setBuffered([[0, 4]]);
  run.video.dispatch('playing');
  await run.advance(0);
  const ahead = [...range(2, 5), 15, ...range(6, 14)];
  assert.deepEqual(segments(run), ahead, 'the 30 s hls.js would fetch one by one (the next skips and the 30 s skip first), minus what is buffered');
  const first = run.requests.find(r => /\.m4s$/.test(r.path));
  assert.equal(first.url, segmentUrl(1, 2), 'the same token as hls.js');
  assert.deepEqual({ ...first.init }, { priority: 'low' }, 'a plain GET: no headers, no preflight');

  // The game's moments (Kill 0:30, Dragon 5:12, the note at 6:40, Baron 15:20) wait for a full buffer.
  await run.advance(5000);
  assert.deepEqual(segments(run), ahead);
  run.video.setBuffered([[0, 40]]);
  await run.advance(1000);
  assert.deepEqual(segments(run).slice(14), [156, 200, 460], 'nearest the playhead first; the Kill at 0:30 is already there');
  assert.equal(run.mints(), 1, 'one play, however much is fetched ahead');
  assert.equal(run.message(), null);

  // Playing on asks once a second, and only for what is new.
  run.playTo(10);
  await run.advance(0);
  assert.deepEqual(segments(run).slice(17), [20], 'only where a 30 s skip would land now');
  await run.advance(1000);
  run.video.setBuffered([[0, 44]]);
  run.playTo(16);
  await run.advance(0);
  assert.deepEqual(segments(run).slice(18), [23, 22], 'and the next segment past the buffer, before hls.js asks for it');
});

test('a skip or a jump fetches around the new place once it rests, and leaves hls.js its own segments', async () => {
  const run = await playing();
  const hls = run.hls();
  hls.level(1000, false, fragments(500));
  run.video.currentTime = 100;
  run.video.dispatch('seeking');
  run.video.currentTime = 400;
  run.video.dispatch('seeking');
  run.video.currentTime = 600; // a drag along the lane: only where it rests counts
  run.video.dispatch('seeking');
  hls.emit(run.FakeHls.Events.FRAG_LOADING, { frag: { url: segmentUrl(1, 300) } });
  hls.emit(run.FakeHls.Events.FRAG_LOADED, { frag: { url: segmentUrl(1, 301) } });
  await run.advance(79);
  assert.deepEqual(segments(run), []);
  await run.advance(1);
  assert.deepEqual(segments(run), [...range(302, 305), ...range(299, 295), 315, ...range(306, 314), ...range(294, 290)],
    'ahead first (hls.js has 300 and 301), the 10 s behind for a skip back, the 30 s skip, the rest ahead, 10 s further back');

  // A skip back 10 s lands on what is there, and reaches 10 s further back; nothing twice.
  run.video.currentTime = 590;
  run.video.dispatch('seeking');
  await run.advance(80);
  assert.deepEqual(segments(run).slice(24), range(289, 285));
});

test('a pointer resting on the lane fetches the second under it before the press', async () => {
  let controls = null;
  const run = await playing({ globals: { RevuVodControls: { attach(o) { controls = o; return { paint() {}, penPressed() {} }; } } } });
  run.hls().level(1000, false, fragments(500));
  run.video.setBuffered([[0, 40]]);
  controls.hover(250);
  controls.hover(501.5); // still moving
  await run.advance(99);
  assert.deepEqual(segments(run), []);
  await run.advance(1);
  assert.deepEqual(segments(run).slice(0, 2), [250, 251], 'first of all; the next segment too, this close to the end of one');
  const count = segments(run).length;

  controls.hover(700);
  controls.hover(null); // the pointer left
  await run.advance(1000);
  assert.equal(segments(run).includes(350), false);
  assert.equal(segments(run).length, count);
});

test('read-ahead stays quiet on mobile data, with Save-Data, in a hidden paused tab, and follows a new token', async () => {
  for (const connection of [{ saveData: true }, { type: 'cellular' }]) {
    const sparing = await playing();
    sparing.context.navigator.connection = connection;
    sparing.hls().level(1000, false, fragments(500));
    sparing.video.dispatch('playing');
    sparing.video.dispatch('seeking');
    await sparing.advance(5000);
    assert.deepEqual(segments(sparing), [], JSON.stringify(connection));
  }

  const run = await playing();
  const hls = run.hls();
  hls.level(1000, false, fragments(500));
  run.document.hidden = true;
  run.video.pause();
  run.document.dispatch('visibilitychange');
  assert.deepEqual(hls.last(), ['stopLoad']);
  run.video.dispatch('seeking');
  await run.advance(5000);
  assert.deepEqual(segments(run), [], 'loading is stopped: nothing ahead either');

  run.document.hidden = false;
  run.document.dispatch('visibilitychange');
  run.video.dispatch('seeking');
  await run.advance(80);
  const fromStart = [...range(0, 5), 15, ...range(6, 14)];
  assert.deepEqual(segments(run), fromStart);
  assert.equal(run.requests.filter(r => /\.m4s$/.test(r.path)).every(r => r.url.startsWith(BASE)), true);

  // The token ends (a 403 on hls.js's own request): a new one, new addresses, fetched again.
  await run.advance(61000);
  hls.fail(run.FakeHls.ErrorTypes.NETWORK_ERROR, true, 403);
  await run.advance(0);
  assert.equal(run.mints(), 2);
  run.video.dispatch('seeking');
  await run.advance(80);
  assert.equal(segments(run).length, 16, 'the old list is for the old token');
  hls.level(1000, false, fragments(500, 2));
  run.video.dispatch('seeking');
  await run.advance(80);
  assert.deepEqual(run.requests.filter(r => /\.m4s$/.test(r.path)).slice(16).map(r => r.url), fromStart.map(seq => segmentUrl(2, seq)));
});

test('a refused segment never becomes the page\'s problem, and a game still uploading is fetched only up to its edge', async () => {
  const refused = await playing({ routes: { 'GET segment': { httpStatus: 403, body: { error: 'bad_token' } } } });
  refused.hls().level(1000, false, fragments(500));
  refused.video.dispatch('playing');
  await refused.advance(0);
  assert.deepEqual(segments(refused), [0, 1], 'both lanes were refused: no more for 20 s');
  await refused.advance(19000);
  assert.deepEqual(segments(refused), [0, 1]);
  assert.equal(refused.message(), null);
  assert.equal(refused.state(), 'state-game');

  const live = openPage({ routes: { [META]: hlsMeta() } });
  await live.advance(0);
  live.$('vod-play').click();
  await live.advance(0);
  live.hls().level(120, true, fragments(60));
  live.video.currentTime = 100;
  live.video.dispatch('seeking');
  await live.advance(80);
  assert.deepEqual(segments(live), [...range(50, 55), ...range(49, 45), ...range(56, 59), ...range(44, 40)], 'what is uploaded, and no further');
});
