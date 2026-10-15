// A shared full game that plays while it uploads (format "hls"), with a fake hls.js: which
// player each browser gets, nothing loaded before play, a page that keeps up with the upload
// before anyone presses play, waiting, Play when ready, the rebuffer policy, catching up with
// the upload, and jumping to a moment when it arrives. Errors are in vod-live-errors.test.mjs.
import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { BROWSERS, DASHES, DOC, NOW_S, SPACES, TEST_INTEGRITY, hlsMeta, openPage, playlist, sources } from './vod-harness.mjs';

function pure() {
  const context = { URLSearchParams };
  vm.createContext(context);
  vm.runInContext(sources.lib, context);
  vm.runInContext(sources.player, context);
  return { V: context.RevuVod, P: context.RevuVodPlayer };
}

const META = 'GET /vod-meta/Abc1234';
// The clock time is in the viewer's own locale and time zone.
const SLOW = /^The upload is slower than playback\. Smooth from about \S.* \(in \d+ min\), or play now with pauses\.$/;
const flat = text => String(text).replace(SPACES, ' ');

test('player choice: hls.js wherever MediaSource plays the share, Safari plays finished games itself, no MediaSource waits', () => {
  const { P } = pure();
  const caps = name => ({ mse: BROWSERS[name].mse, mms: BROWSERS[name].mms, native: !!BROWSERS[name].native });
  assert.deepEqual(Object.keys(BROWSERS).map(name => [name, P.choose(caps(name), false), P.choose(caps(name), true)]), [
    ['chrome', 'hlsjs', 'hlsjs'], // says "maybe" to native HLS, but skips segments and can't seek with it
    ['firefox', 'hlsjs', 'hlsjs'],
    ['safari', 'hlsjs', 'native'], // native only once finished (WebKit 278865: EVENT playlists can't seek)
    ['iphone', 'hlsjs', 'native'], // iOS 17.1+: ManagedMediaSource while uploading
    ['oldIos', 'wait', 'native'], // iOS before 17.1: waits for the finished game
    ['none', 'none', 'none'],
  ]);
  assert.deepEqual(Object.keys(BROWSERS).map(name => P.autoplayAfterClick(caps(name))), [true, true, false, false, false, true]);
  assert.deepEqual({ ...P.hlsConfig() }, { autoStartLoad: false, startPosition: 0, enableWorker: false, lowLatencyMode: false,
    liveDurationInfinity: false, maxBufferLength: 30, backBufferLength: 90 });
  assert.equal(P.HLS_SRC, '/vendor/hls-1.7.3/hls.light.min.js');
  assert.equal(P.CODECS, 'video/mp4; codecs="avc1.64002a,mp4a.40.2"');
});

test('the playlist URL is built only from a checked id and a checked token', () => {
  const { P } = pure();
  const api = 'https://clips.revu.lol';
  const good = `${NOW_S + 43200}.abcdefghij_KLMNOPQR-st`;
  assert.equal(P.mintUrl(api, 'Abc1234'), 'https://clips.revu.lol/vod-play/Abc1234');
  assert.equal(P.playlistUrl(api, 'Abc1234', good), `https://clips.revu.lol/vod-hls/Abc1234/${good}/index.m3u8`);
  for (const id of ['Abc123', 'Abc12345', '../x', 'Abc 234', 'Abc1234/']) {
    assert.equal(P.mintUrl(api, id), '', id);
    assert.equal(P.playlistUrl(api, id, good), '', id);
  }
  for (const bad of ['', null, undefined, 'abc', `${NOW_S}.short`, `${good}x`, `x${good}`, `${NOW_S + 43200}.abcdefghij/KLMNOPQR-st`,
    `${NOW_S + 43200}.abcdefghij.KLMNOPQR-st`, `${NOW_S + 43200}.abcdefghij%2FLMNOPQR-s`, `${NOW_S}0.abcdefghij_KLMNOPQR-st`]) {
    assert.equal(P.playlistUrl(api, 'Abc1234', bad), '', String(bad));
  }
  assert.deepEqual([429, 503, 404, 403, 500].map(status => P.mintKind(status, 'x')), ['limited', 'paused', 'gone', 'unplayable', 'unplayable']);
  assert.equal(P.mintKind(404, 'not_ready'), 'notready');
  assert.deepEqual([429, 503, 404, 410, 403, 500, 0].map(code => P.errorKind(code, true)),
    ['limited', 'paused', 'gone', 'unplayable', 'unplayable', 'unplayable', 'unplayable'], 'no 410: there are no generations to re-attach to');
  assert.equal(P.errorKind(0, false), 'offline');
  assert.equal(P.errorKind(undefined, false), 'offline');
});

test('meta states, waiting copy, polling and the line under the player', () => {
  const { V } = pure();
  assert.deepEqual([hlsMeta({ playable: false }), hlsMeta(), hlsMeta({ play_limited: true }), hlsMeta({ serving: false }),
    hlsMeta({ status: 'ready', ended: true }), hlsMeta({ ended: true })].map(V.metaState), ['waiting', 'live', 'limited', 'paused', 'ok', 'ok']);
  const waiting = (age, commit = null) => V.waitingKind(hlsMeta({ created_at: NOW_S - age, now: NOW_S, last_commit_age_s: commit }), 0);
  assert.deepEqual([waiting(0), waiting(29), waiting(30), waiting(179), waiting(180), waiting(900, 200), waiting(900, 5)],
    ['soon', 'soon', 'longer', 'longer', 'notstarted', 'longer', 'soon']);
  assert.deepEqual({ ...V.WAITING.soon }, { title: 'Starting soon', copy: 'This game starts here in a few seconds, while the rest uploads. You can leave this page open.' });
  assert.equal(V.WAITING.longer.title, 'Taking longer than usual');
  assert.deepEqual({ ...V.WAITING.notstarted }, { title: "The upload hasn't started", copy: 'The sharer may have closed Revu.' });
  assert.deepEqual([[0], [59999], [60000], [599999], [600000]].map(([ms]) => V.hlsPollDelay('waiting', ms)), [2000, 2000, 10000, 10000, 30000]);
  assert.deepEqual(['ready', 'playing', 'hold', ''].map(phase => V.hlsPollDelay(phase, 0)), [10000, 30000, 15000, 0]);

  const base = { gone: false, ended: false, truncated: false, paused: false, stalled: false, started: true, hold: null, atEdge: false,
    edge: 766, duration: 2256, rate: 3, position: 100, rebufferOn: true, nowMs: Date.UTC(2026, 9, 14, 14) };
  const say = (overrides, options) => V.liveLine({ ...base, ...overrides }, options).text;
  assert.equal(say({ started: false, edge: 8 }), 'Ready from the start. 0:08 of 37:36 uploaded so far');
  assert.equal(say({}), 'Watchable up to 12:46 of 37:36 · still uploading');
  assert.equal(say({ hold: 'caughtup' }), 'Waiting for more of the game to upload');
  assert.equal(say({ atEdge: true }), 'Waiting for more of the game to upload');
  assert.equal(say({ paused: true, edge: 760 }), 'The sharer started another game. You can watch up to 12:40; the rest uploads after it.');
  assert.equal(say({ stalled: true, edge: 760 }), 'The upload has stalled. You can watch up to 12:40.');
  assert.equal(say({ ended: true }), 'The whole game is here.');
  assert.equal(say({ ended: true, truncated: true, edge: 2110 }), 'The sharer stopped before the end. Shared up to 35:10.');
  assert.equal(say({ gone: true }), 'The sharer stopped sharing this game.');
  // 0.85x: from 12:46 the edge reaches the end in (2256 - 766) / 0.85 = 1753 s. Playing the rest from 1:40
  // takes 2156 s, so that viewer never catches up; from 11:40 it takes 1556 s: smooth in 197 s, at 14:03:17.
  assert.equal(V.liveLine({ ...base, rate: 0.85 }).kind, 'watchable');
  const slow = V.liveLine({ ...base, rate: 0.85, position: 700 }, { locale: 'en-US', timeZone: 'UTC' });
  assert.equal(flat(slow.text), 'The upload is slower than playback. Smooth from about 2:03 PM (in 3 min), or play now with pauses.');
  // Ten seconds on, the upload and the clock have both moved: the same time, rounded the same way.
  assert.equal(flat(V.liveLine({ ...base, rate: 0.85, position: 700, edge: 766 + 8.5, nowMs: base.nowMs + 10000 },
    { locale: 'en-US', timeZone: 'UTC' }).text), flat(slow.text));
  assert.deepEqual([slow.kind, slow.action, slow.after], ['slow', 'play now with pauses', '.']);
  assert.equal(slow.before + slow.action + slow.after, slow.text);
  assert.equal(V.liveLine({ ...base, rate: 0.85, position: 700, rebufferOn: false }).kind, 'watchable', 'turned off: no offer');
  assert.equal(V.liveLine({ ...base, rate: 0.85, hold: 'rebuffer' }).kind, 'slow');
  for (const kind of [{}, { gone: true }, { ended: true }, { paused: true }, { stalled: true }, { rate: 0.5, position: 700 }]) {
    assert.doesNotMatch(say(kind), DASHES);
  }

  const ranges = list => ({ length: list.length, start: i => list[i][0], end: i => list[i][1] });
  assert.deepEqual([[100, [[90, 130]]], [130, [[90, 130]]], [10, [[0, 5], [9.9, 20]]], [50, [[0, 5]]], [0, null]]
    .map(([t, list]) => V.bufferedAhead(list && ranges(list), t)), [30, 0, 10, 0, 0]);
  assert.equal(Math.round(V.smoothWait(2256, 766, 0.85, 700)), 197);
  assert.equal(V.smoothWait(2256, 766, 1.2, 700), 0, 'an upload faster than playback never needs a wait');
  assert.deepEqual([[5, 0.8], [1.5, 0.8], [1.5, 0.69], [1.5, 1.5]].map(([lead, rate]) => {
    const hold = V.holdAtEdge({ lead, rate, rebufferOn: true });
    return hold && { ...hold };
  }), [null, { kind: 'rebuffer', lead: 30 }, { kind: 'rebuffer', lead: 60 }, null]);
  assert.deepEqual({ ...V.holdAtEdge({ lead: 1, rate: 0.8, rebufferOn: false }) }, { kind: 'caughtup', lead: 6 });
  assert.deepEqual({ ...V.holdAtEdge({ lead: 1, rate: 2, paused: true }) }, { kind: 'caughtup', lead: 6 });
  assert.deepEqual({ ...V.holdAtEdge({ lead: 1, rate: 2, stalled: true }) }, { kind: 'caughtup', lead: 6 });

  const items = V.laneItems(DOC, 1000, { edge: 120, rate: 2, armed: 920 });
  assert.deepEqual([...items].map(i => [i.label, i.pending, i.eta, i.armed]),
    [['Kill', false, null, false], ['Dragon', true, 2, false], ['Baron', true, 7, true], ['Clip', true, 3, false]]);
  assert.equal(V.pendingSuffix(items[1]), ', not uploaded yet, about 2 min');
  assert.equal(V.jumpText(items[2]), 'Jumps to Baron at 15:20 when it arrives, in about 7 min.');
  assert.deepEqual([...V.laneItems(DOC, 1000, { edge: 120, rate: null })].map(i => i.eta), [null, null, null, null], 'no rate, no estimate');
  assert.deepEqual([...V.laneItems(DOC, 1000)].map(i => i.pending), [false, false, false, false], 'finished: nothing waits');
  assert.equal(V.pendingSuffix(V.laneItems(DOC, 1000, { edge: 120, rate: null })[1]), ', not uploaded yet');
});

test('a playable game loads nothing from the proxy before play, then mints one token and starts at 0:00', async () => {
  const run = openPage({ routes: { [META]: hlsMeta() } });
  await run.advance(0);
  assert.equal(run.state(), 'state-game');
  assert.deepEqual(run.requests.map(r => `${r.method} ${r.path}`), [META]);
  assert.deepEqual(run.scripts.map(s => [s.src, s.integrity]), [['/vendor/hls-1.7.3/hls.light.min.js', TEST_INTEGRITY]]);
  const hls = run.hls();
  assert.deepEqual(hls.calls, [['attachMedia']], 'attached, nothing loaded');
  assert.deepEqual({ ...hls.config }, { autoStartLoad: false, startPosition: 0, enableWorker: false, lowLatencyMode: false,
    liveDurationInfinity: false, maxBufferLength: 30, backBufferLength: 90 });
  assert.equal(run.$('vod-play').hidden, false);
  assert.equal(run.$('vod-play-label').textContent, 'Play from the start');
  assert.equal(run.video.controls, false, 'the button stands in for the controls until the first play');
  assert.equal(run.lineText(), 'Ready from the start. 2:00 of 16:40 uploaded so far');
  assert.equal(run.$('vod-live-say').textContent, 'Ready from the start. 2:00 of 16:40 uploaded so far');
  assert.equal(run.document.title, 'Ready to watch · Jinx vs Caitlyn');
  assert.equal(run.$('vod-lane-uploaded').hidden, false);
  assert.equal(run.$('vod-lane-uploaded').style.width, '12%');

  await run.advance(10 * 60000);
  assert.equal(run.mints(), 0, 'an open tab counts no play');
  assert.deepEqual(hls.calls, [['attachMedia']]);
  assert.equal(run.metaPolls(), 61, 'every 10 s while playable and not playing');

  run.$('vod-play').click();
  await run.advance(0);
  const mint = run.requests.find(r => r.method === 'POST');
  assert.equal(mint.url, 'https://clips.revu.lol/vod-play/Abc1234');
  assert.deepEqual({ ...mint.init }, { method: 'POST' }, 'no body, no headers');
  assert.deepEqual(hls.calls, [['attachMedia'], ['loadSource', playlist(1)], ['startLoad', 0]]);
  assert.equal(run.video.plays, 1);
  assert.equal(run.video.controls, true);
  assert.equal(run.$('vod-play').hidden, true);
  assert.equal(run.document.title, 'Jinx vs Caitlyn - Revu');
  assert.equal(run.lineText(), 'Watchable up to 2:00 of 16:40 · still uploading');
  assert.deepEqual(run.pendingPolls(), [30000], 'playing: hls.js reloads the playlist, /vod-meta every 30 s');
  hls.level(140);
  assert.equal(run.lineText(), 'Watchable up to 2:20 of 16:40 · still uploading');
  assert.equal(run.mints(), 1);
});

test('THE BLOCKER: a live page nobody plays keeps up with the upload for 3 minutes and unlocks moments as they arrive', async () => {
  let start = null;
  const run = openPage({ routes: { [META]: ({ now }) => {
    if (start === null) start = now;
    return hlsMeta({ media_seconds: 120 + 4 * ((now - start) / 1000), ingest_x: 4, last_commit_age_s: 1, now: now / 1000 });
  } } });
  await run.advance(0);
  const dragon = run.mark('Dragon at 5:12');
  assert.equal(dragon.getAttribute('aria-label'), 'Dragon at 5:12, not uploaded yet, about 1 min');
  assert.equal(dragon.getAttribute('aria-pressed'), 'false');
  assert.equal(dragon.className, 'vod-mark is-gold is-pending');
  assert.equal(run.mark('Baron at 15:20').getAttribute('aria-label'), 'Baron at 15:20, not uploaded yet, about 4 min');
  const note = run.$('vod-notes-list').children[0].children[0];
  assert.equal(note.className, 'vod-note is-pending');
  assert.equal(note.title, '6:40 Clip (not uploaded yet, about 2 min)');
  assert.equal(note.children.at(-1).textContent, ', not uploaded yet, about 2 min', 'a visually hidden suffix keeps the note text as its name');

  await run.advance(3 * 60000);
  assert.equal(run.metaPolls(), 19, 'asked every 10 s without a press of play');
  assert.equal(run.lineText(), 'Ready from the start. 14:00 of 16:40 uploaded so far', 'still uploading, never "stalled"');
  assert.equal(run.mark('Dragon at 5:12'), dragon, 'the same button: keyboard focus survives');
  assert.equal(dragon.getAttribute('aria-label'), 'Dragon at 5:12');
  assert.equal(dragon.getAttribute('aria-pressed'), null);
  assert.equal(dragon.className, 'vod-mark is-gold');
  assert.equal(note.className, 'vod-note', 'the 6:40 note arrived too');
  assert.equal(note.title, '');
  assert.equal(note.children.at(-1).textContent, '');
  assert.equal(run.mark('Baron at 15:20').getAttribute('aria-label'), 'Baron at 15:20, not uploaded yet, about 1 min');
  assert.equal(run.$('vod-lane-uploaded').style.width, '84%');
  assert.equal(run.mints(), 0);
  assert.deepEqual(run.hls().calls, [['attachMedia']]);
  await run.advance(30000);
  assert.equal(run.mark('Baron at 15:20').getAttribute('aria-label'), 'Baron at 15:20');
});

test('waiting for the first part: Starting soon, Taking longer than usual, then The upload has not started; 2 s, 10 s, 30 s polls', async () => {
  const run = openPage({ routes: { [META]: ({ now }) => hlsMeta({ playable: false, media_seconds: 0, ingest_x: null, last_commit_age_s: null,
    created_at: NOW_S - 5, now: now / 1000 }) } });
  await run.advance(0);
  assert.equal(run.state(), 'state-uploading');
  assert.equal(run.$('upload-title').textContent, 'Starting soon');
  assert.equal(run.$('upload-copy').textContent, 'This game starts here in a few seconds, while the rest uploads. You can leave this page open.');
  assert.equal(run.document.title, 'Starting soon - Revu');
  assert.equal(run.$('upload-meter').hidden, true, 'no meter: it starts a few seconds in');
  assert.equal(run.$('vod-when-ready-row').hidden, false);
  assert.equal(run.scripts.length, 1, 'the player is fetched while waiting');
  assert.deepEqual(run.pendingPolls(), [2000]);
  await run.advance(24000);
  assert.equal(run.$('upload-title').textContent, 'Starting soon', '29 s since the share started');
  await run.advance(2000);
  assert.equal(run.$('upload-title').textContent, 'Taking longer than usual');
  assert.equal(run.$('upload-copy').textContent, 'This game starts here as soon as the upload picks up. You can leave this page open.');
  await run.advance(34000);
  assert.equal(run.metaPolls(), 31, 'every 2 s for the first minute');
  assert.deepEqual(run.pendingPolls(), [10000]);
  await run.advance(110000);
  assert.equal(run.$('upload-title').textContent, 'Taking longer than usual', '175 s');
  await run.advance(10000);
  assert.equal(run.$('upload-title').textContent, "The upload hasn't started");
  assert.equal(run.$('upload-copy').textContent, 'The sharer may have closed Revu.');
  assert.equal(run.document.title, "The upload hasn't started - Revu");
  await run.advance(420000);
  assert.deepEqual(run.pendingPolls(), [30000], 'every 30 s after 10 minutes');

  // A hidden tab never asks; shown again, it asks at once.
  const before = run.metaPolls();
  run.document.hidden = true;
  await run.advance(5 * 60000);
  assert.equal(run.metaPolls(), before);
  run.document.hidden = false;
  run.document.dispatch('visibilitychange');
  await run.advance(0);
  assert.equal(run.metaPolls(), before + 1);
  assert.equal(run.mints(), 0);
});

test('Play when ready starts the game by itself in Chromium once it is playable; Safari shows the button instead', async () => {
  for (const browser of ['chrome', 'firefox']) {
    let playable = false;
    const run = openPage({ browser, routes: { [META]: () => (playable ? hlsMeta({ media_seconds: 8 }) : hlsMeta({ playable: false, media_seconds: 4 })) } });
    await run.advance(0);
    run.$('vod-when-ready').click();
    assert.equal(run.$('vod-when-ready').checked, true);
    playable = true;
    await run.advance(2000);
    assert.equal(run.state(), 'state-game');
    assert.equal(run.mints(), 1, browser);
    assert.deepEqual(run.hls().calls, [['attachMedia'], ['loadSource', playlist(1)], ['startLoad', 0]]);
    assert.equal(run.video.paused, false, `${browser} plays after the click on the toggle`);
    assert.equal(run.$('vod-play').hidden, true);
    assert.equal(run.document.title, 'Jinx vs Caitlyn - Revu');
  }

  let playable = false;
  const safari = openPage({ browser: 'safari', routes: { [META]: () => (playable ? hlsMeta({ media_seconds: 8 }) : hlsMeta({ playable: false })) } });
  await safari.advance(0);
  safari.$('vod-when-ready').click();
  playable = true;
  await safari.advance(2000);
  assert.equal(safari.mints(), 0, 'Safari: no token until the press');
  assert.equal(safari.$('vod-play').hidden, false);
  assert.equal(safari.document.title, 'Ready to watch · Jinx vs Caitlyn');
  assert.equal(safari.$('vod-live-say').textContent, 'Ready from the start. 0:08 of 16:40 uploaded so far');
  safari.$('vod-play').click();
  await safari.advance(0);
  assert.equal(safari.mints(), 1);
  assert.equal(safari.video.paused, false);

  // Without the toggle, the game waits for the press.
  playable = false;
  const plain = openPage({ routes: { [META]: () => (playable ? hlsMeta() : hlsMeta({ playable: false })) } });
  await plain.advance(0);
  playable = true;
  await plain.advance(2000);
  assert.equal(plain.mints(), 0);
  assert.equal(plain.$('vod-play').hidden, false);
});

test('rebuffer: an upload slower than playback pauses at the edge until 30 s ahead (60 s below 0.7x)', async () => {
  let media = 120, rate = 0.8;
  const run = openPage({ routes: { [META]: () => hlsMeta({ media_seconds: media, ingest_x: rate }) } });
  await run.advance(0);
  assert.match(run.lineText(), SLOW, 'offered before play');
  run.$('vod-play').click();
  await run.advance(0);
  const hls = run.hls();
  run.playTo(100);
  assert.equal(run.video.paused, false);
  run.playTo(118.5);
  assert.equal(run.video.paused, true, 'paused 1.5 s before the edge');
  assert.deepEqual(hls.last(), ['stopLoad']);
  assert.match(run.lineText(), SLOW);
  assert.equal(run.$('vod-live-action').hidden, false);
  assert.deepEqual(run.pendingPolls(), [15000]);
  media = 140;
  await run.advance(15000);
  assert.equal(run.video.paused, true, '21.5 s ahead is not enough');
  assert.deepEqual(hls.last(), ['stopLoad']);
  media = 149;
  await run.advance(15000);
  assert.equal(run.video.paused, false, '30.5 s ahead: playing again');
  assert.deepEqual(hls.last(), ['startLoad', 118.5]);
  assert.deepEqual(run.pendingPolls(), [30000]);

  rate = 0.6;
  await run.advance(30000);
  run.playTo(147.5);
  assert.equal(run.video.paused, true);
  media = 200;
  await run.advance(15000);
  assert.equal(run.video.paused, true, 'below 0.7x it waits for 60 s');
  media = 208;
  await run.advance(15000);
  assert.equal(run.video.paused, false);
  assert.deepEqual(hls.last(), ['startLoad', 147.5]);
});

test('Play now with pauses turns the rebuffer off: playback goes on as each bit arrives', async () => {
  let media = 120;
  const run = openPage({ routes: { [META]: () => hlsMeta({ media_seconds: media, ingest_x: 0.8 }) } });
  await run.advance(0);
  run.$('vod-play').click();
  await run.advance(0);
  const hls = run.hls();
  run.playTo(118.5);
  assert.equal(run.video.paused, true);
  const calls = hls.calls.length;
  run.$('vod-live-action').click();
  assert.equal(run.video.paused, false, 'playing again at once');
  assert.equal(hls.calls.length, calls, 'still at the edge: no reload until more is uploaded');
  assert.equal(run.lineText(), 'Waiting for more of the game to upload');
  assert.deepEqual(run.pendingPolls(), [15000]);
  media = 126;
  await run.advance(15000);
  assert.deepEqual(hls.last(), ['startLoad', 118.5]);
  run.playTo(125);
  assert.equal(run.video.paused, false, 'never paused by the page again');
  assert.deepEqual(hls.last(), ['stopLoad']);
  assert.equal(run.lineText(), 'Waiting for more of the game to upload');
});

test('caught up with a paused or stalled upload: loading stops, /vod-meta every 15 s, and it picks up when more arrives', async () => {
  let reply = hlsMeta({ media_seconds: 120, ingest_x: 3 });
  const run = openPage({ routes: { [META]: () => reply } });
  await run.advance(0);
  run.$('vod-play').click();
  await run.advance(0);
  const hls = run.hls();
  reply = hlsMeta({ media_seconds: 120, ingest_x: 3, paused_reason: 'game', last_commit_age_s: 40 });
  await run.advance(30000);
  assert.equal(run.lineText(), 'The sharer started another game. You can watch up to 2:00; the rest uploads after it.');
  assert.equal(run.$('vod-live-say').textContent, run.lineText());
  run.playTo(118.5);
  assert.deepEqual(hls.last(), ['stopLoad']);
  assert.equal(run.video.paused, false, 'not paused by the page: it plays on when more arrives');
  assert.deepEqual(run.pendingPolls(), [15000]);
  await run.advance(15000);
  assert.deepEqual(hls.last(), ['stopLoad']);
  reply = hlsMeta({ media_seconds: 130, ingest_x: 3 });
  await run.advance(15000);
  assert.deepEqual(hls.last(), ['startLoad', 118.5]);
  assert.equal(run.lineText(), 'Watchable up to 2:10 of 16:40 · still uploading');
  reply = hlsMeta({ media_seconds: 130, ingest_x: 3, last_commit_age_s: 150 });
  await run.advance(30000);
  assert.equal(run.lineText(), 'The upload has stalled. You can watch up to 2:10.');
});

test('a moment that is not uploaded yet arms a jump: the page goes there when it arrives, before or after the first play', async () => {
  let media = 120;
  const run = openPage({ routes: { [META]: () => hlsMeta({ media_seconds: media, ingest_x: 4 }) } });
  await run.advance(0);
  const dragon = run.mark('Dragon at 5:12');
  dragon.click();
  assert.equal(dragon.getAttribute('aria-pressed'), 'true');
  assert.equal(dragon.className, 'vod-mark is-gold is-pending is-armed');
  assert.equal(run.$('vod-jump').hidden, false);
  assert.equal(run.$('vod-jump-text').textContent, 'Jumps to Dragon at 5:12 when it arrives, in about 1 min.');
  assert.equal(run.$('vod-live-say').textContent, 'Jumps to Dragon at 5:12 when it arrives, in about 1 min.');
  dragon.click();
  assert.equal(dragon.getAttribute('aria-pressed'), 'false', 'a second press cancels');
  assert.equal(run.$('vod-jump').hidden, true);
  dragon.click();
  run.$('vod-jump-cancel').click();
  assert.equal(run.$('vod-jump').hidden, true);
  dragon.click();
  assert.equal(run.mints(), 0, 'nothing loads until it arrives');
  media = 320;
  await run.advance(10000);
  assert.equal(run.mints(), 1);
  assert.deepEqual(run.hls().calls, [['attachMedia'], ['loadSource', playlist(1)], ['startLoad', 312]]);
  assert.equal(run.video.paused, false);
  assert.equal(run.$('vod-jump').hidden, true);
  assert.equal(dragon.getAttribute('aria-pressed'), null);

  // While playing, the playlist itself can bring the moment.
  run.playTo(312);
  run.mark('Baron at 15:20').click();
  run.hls().level(930);
  assert.equal(run.video.currentTime, 920);
  assert.equal(run.$('vod-jump').hidden, true);

  // Safari, not started: it arrives, and the button waits for the press.
  media = 120;
  const safari = openPage({ browser: 'safari', routes: { [META]: () => hlsMeta({ media_seconds: media, ingest_x: 4 }) } });
  await safari.advance(0);
  safari.mark('Dragon at 5:12').click();
  media = 320;
  await safari.advance(10000);
  assert.equal(safari.mints(), 0);
  assert.equal(safari.$('vod-play').hidden, false);
  assert.equal(safari.$('vod-play-label').textContent, 'Play');
  safari.$('vod-play').click();
  await safari.advance(0);
  assert.deepEqual(safari.hls().calls.slice(1), [['loadSource', playlist(1)], ['startLoad', 312]]);
});

test('an uploaded moment plays at once, and the first press from the lane starts there', async () => {
  const run = openPage({ routes: { [META]: hlsMeta({ media_seconds: 400 }) } });
  await run.advance(0);
  run.mark('Dragon at 5:12').click();
  await run.advance(0);
  assert.equal(run.mints(), 1);
  assert.deepEqual(run.hls().calls, [['attachMedia'], ['loadSource', playlist(1)], ['startLoad', 312]]);
  assert.equal(run.video.paused, false);
  run.playTo(320);
  run.mark('Kill at 0:30').click();
  assert.equal(run.video.currentTime, 30);
  assert.equal(run.mints(), 1, 'one play, one token');
});
