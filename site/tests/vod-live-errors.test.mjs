// A shared full game that plays while it uploads (format "hls"), with a fake hls.js: a
// rejected token, a share stopped mid-play, the play cap, playback switched off, going
// offline, an hls.js that isn't pinned, finished games on every kind of browser, the end of
// the upload, and the page's markup. The happy paths are in vod-live.test.mjs.
import assert from 'node:assert/strict';
import test from 'node:test';
import { DASHES, finishedMeta, hlsMeta, html, openPage, playlist, sources } from './vod-harness.mjs';

const META = 'GET /vod-meta/Abc1234';
const MINT = 'POST /vod-play/Abc1234';
const UNPLAYABLE = "This video couldn't be played.";
const PAUSED = 'Playback is paused right now. Try again later.';
const LIMITED = 'This game has been watched a lot today. Try again tomorrow.';
const STOPPED = 'The sharer stopped sharing this game.';

async function playing(options = {}) {
  const run = openPage(options);
  await run.advance(0);
  run.$('vod-play').click();
  await run.advance(0);
  return run;
}

test('a rejected token (403) mints a new one and loads again where the viewer was; twice in a minute gives up', async () => {
  const run = await playing({ routes: { [META]: hlsMeta() } });
  const hls = run.hls();
  await run.advance(61000);
  run.playTo(42);
  hls.fail('networkError', true, 403, 'levelLoadError');
  await run.advance(0);
  assert.equal(run.mints(), 2, 'a new token counts a new play');
  assert.deepEqual(hls.calls.slice(-2), [['loadSource', playlist(2)], ['startLoad', 42]]);
  assert.equal(run.message(), null);
  assert.equal(run.video.paused, false);

  hls.fail('networkError', true, 403, 'fragLoadError');
  await run.advance(0);
  assert.equal(run.mints(), 2, 'a token just minted that fails again is not retried');
  assert.equal(run.message(), UNPLAYABLE);
  assert.equal(run.$('vod-retry').hidden, false);
  assert.deepEqual(hls.last(), ['destroy']);
});

test('a share stopped mid-play (404) plays out what is buffered, then says it stopped', async () => {
  const run = await playing({ routes: { [META]: hlsMeta() } });
  const hls = run.hls();
  run.playTo(100);
  run.video.setBuffered([[90, 130]]);
  hls.fail('networkError', true, 404, 'levelLoadError');
  await run.advance(0);
  assert.equal(run.lineText(), STOPPED);
  assert.equal(run.$('vod-live-say').textContent, STOPPED);
  assert.equal(run.message(), null, 'the buffer still plays');
  assert.equal(hls.calls.some(call => call[0] === 'destroy'), false);
  assert.deepEqual(run.pendingPolls(), [], 'nothing left to ask');
  run.playTo(120);
  assert.equal(run.message(), null);
  run.playTo(129.8);
  assert.equal(run.message(), STOPPED);
  assert.equal(run.$('vod-retry').hidden, true);
  assert.deepEqual(hls.last(), ['destroy']);

  // Found by a poll before anyone played: the page says so.
  let reply = hlsMeta();
  const idle = openPage({ routes: { [META]: () => reply } });
  await idle.advance(0);
  reply = null;
  await idle.advance(10000);
  assert.equal(idle.state(), 'state-missing');
  assert.equal(idle.$('state-title').textContent, 'Sharing stopped');
  assert.equal(idle.$('state-copy').textContent, STOPPED);
  assert.deepEqual(idle.hls().calls.slice(-1), [['destroy']]);
  assert.deepEqual(idle.pendingPolls(), []);
});

test('the play cap refuses new plays only: 429 at mint says so, a capped meta never stops someone watching', async () => {
  const capped = await playing({ routes: { [META]: hlsMeta(), [MINT]: { httpStatus: 429, body: { error: 'play_limit' } } } });
  assert.equal(capped.message(), LIMITED);
  assert.equal(capped.$('vod-retry').hidden, true);
  assert.equal(capped.hls().calls.some(call => call[0] === 'loadSource'), false);
  assert.deepEqual(capped.hls().last(), ['destroy']);
  assert.equal(capped.$('vod-live').hidden, true);

  let reply = hlsMeta();
  const watching = await playing({ routes: { [META]: () => reply } });
  reply = hlsMeta({ play_limited: true, media_seconds: 160 });
  await watching.advance(30000);
  assert.equal(watching.message(), null);
  assert.equal(watching.lineText(), 'Watchable up to 2:40 of 16:40 · still uploading');

  const late = openPage({ routes: { [META]: hlsMeta({ play_limited: true }) } });
  await late.advance(0);
  assert.equal(late.state(), 'state-missing');
  assert.equal(late.$('state-title').textContent, 'Playback limit reached');
  assert.equal(late.mints(), 0);
});

test('playback switched off (503): one soft recheck while hls.js retries, then paused with Try again', async () => {
  // A blip: the recheck finds playback on, so nothing changes while hls.js retries.
  const blip = await playing({ routes: { [META]: hlsMeta() } });
  const polls = blip.metaPolls();
  blip.hls().fail('networkError', false, 503);
  await blip.advance(0);
  assert.equal(blip.metaPolls(), polls + 1, 'one recheck');
  assert.equal(blip.message(), null);
  blip.hls().fail('networkError', false, 502);
  await blip.advance(0);
  assert.equal(blip.metaPolls(), polls + 1, 'at most one recheck every 30 s');

  // Switched off: the first retried 503 finds it off, well before hls.js gives up.
  let reply = hlsMeta();
  const run = await playing({ routes: { [META]: () => reply } });
  const hls = run.hls();
  reply = hlsMeta({ serving: false });
  hls.fail('networkError', false, 503);
  await run.advance(0);
  assert.equal(run.message(), PAUSED);
  assert.equal(run.$('vod-retry').hidden, false);
  assert.deepEqual(hls.last(), ['destroy']);
  assert.equal(run.$('vod-live').hidden, true);

  const fatal = await playing({ routes: { [META]: hlsMeta() } });
  fatal.hls().fail('networkError', true, 503);
  await fatal.advance(0);
  assert.equal(fatal.message(), PAUSED);

  const mint = await playing({ routes: { [META]: hlsMeta(), [MINT]: { httpStatus: 503, body: { error: 'vod_serving_disabled' } } } });
  assert.equal(mint.message(), PAUSED);
  assert.equal(mint.$('vod-retry').hidden, false);
});

test('offline: the page says so and picks up where it was once back online; Try again does the same', async () => {
  const run = await playing({ routes: { [META]: hlsMeta() } });
  run.playTo(64);
  run.context.navigator.onLine = false;
  run.hls().fail('networkError', true, 0, 'fragLoadError');
  await run.advance(0);
  assert.equal(run.message(), "You're offline. The game picks up again when you're back online.");
  assert.equal(run.$('vod-retry').hidden, false);
  run.online(true);
  await run.advance(0);
  assert.equal(run.message(), null);
  assert.equal(run.FakeHls.instances.length, 2, 'a fresh player');
  assert.deepEqual(run.hls().calls, [['attachMedia'], ['loadSource', playlist(2)], ['startLoad', 64]]);
  assert.equal(run.video.paused, false);

  run.playTo(70);
  run.hls().fail('mediaError', true);
  assert.deepEqual(run.hls().last(), ['recoverMediaError'], 'one media error is recovered');
  run.hls().fail('mediaError', true);
  await run.advance(0);
  assert.equal(run.message(), UNPLAYABLE);
  run.$('vod-retry').click();
  await run.advance(0);
  assert.equal(run.message(), null);
  assert.deepEqual(run.hls().calls, [['attachMedia'], ['loadSource', playlist(3)], ['startLoad', 70]]);
});

test('an hls.js without its SRI pin is never loaded: the page fails closed and mints nothing', async () => {
  const run = openPage({ pinned: false, routes: { [META]: hlsMeta() } });
  await run.advance(0);
  assert.deepEqual(run.scripts, [], 'no script element at all');
  assert.equal(run.message(), UNPLAYABLE);
  assert.equal(run.$('vod-retry').hidden, false);
  assert.equal(run.$('vod-live').hidden, true);
  assert.equal(run.document.title, 'Jinx vs Caitlyn - Revu', 'no "Ready to watch" over a message');
  assert.equal(run.video.src, '', 'never the browser\'s own player either');
  run.$('vod-retry').click();
  await run.advance(0);
  assert.equal(run.message(), UNPLAYABLE);
  assert.deepEqual(run.scripts, []);
  assert.equal(run.mints(), 0, 'no play is counted for a player that cannot run');

  // A file that fails its SRI check (or doesn't load) is the same.
  const tampered = openPage({ hlsLoads: false, routes: { [META]: hlsMeta() } });
  await tampered.advance(0);
  assert.equal(tampered.scripts.length, 1);
  assert.equal(tampered.message(), UNPLAYABLE);
  assert.equal(tampered.video.src, '');
});

test('hls.js turns remote playback off itself where it uses ManagedMediaSource; the page never touches it', async () => {
  const iphone = openPage({ browser: 'iphone', routes: { [META]: hlsMeta() } });
  await iphone.advance(0);
  assert.equal(iphone.hls().calls[0][0], 'attachMedia');
  assert.equal(iphone.video.disableRemotePlayback, true);
  const chrome = openPage({ routes: { [META]: hlsMeta() } });
  await chrome.advance(0);
  assert.equal(chrome.video.disableRemotePlayback, undefined);
  assert.doesNotMatch(Object.values(sources).join('\n'), /disableRemotePlayback/);
});

test('finished games: Safari and old iPhones play natively, Chromium through hls.js, MP4 shares as before', async () => {
  const safari = openPage({ browser: 'safari', routes: { [META]: finishedMeta() } });
  await safari.advance(0);
  assert.deepEqual(safari.scripts, [], 'no hls.js');
  assert.equal(safari.$('vod-play').hidden, false);
  assert.equal(safari.$('vod-live').hidden, true, 'a finished game has nothing to say');
  assert.equal(safari.video.src, '');
  safari.$('vod-play').click();
  await safari.advance(0);
  assert.equal(safari.mints(), 1);
  assert.equal(safari.video.src, playlist(1));
  assert.equal(safari.video.paused, false);
  assert.deepEqual(safari.pendingPolls(), []);

  const chrome = openPage({ routes: { [META]: finishedMeta() } });
  await chrome.advance(0);
  assert.equal(chrome.scripts.length, 1);
  chrome.$('vod-play').click();
  await chrome.advance(0);
  assert.deepEqual(chrome.hls().calls, [['attachMedia'], ['loadSource', playlist(1)], ['startLoad', 0]]);

  // iOS before 17.1 waits for the finished game (no MediaSource), then plays it natively.
  let reply = hlsMeta();
  const old = openPage({ browser: 'oldIos', routes: { [META]: () => reply } });
  await old.advance(0);
  assert.equal(old.state(), 'state-uploading');
  assert.equal(old.$('upload-title').textContent, 'Still uploading, 12%');
  assert.equal(old.$('upload-copy').textContent, 'This game plays here as soon as the upload finishes. You can leave this page open.');
  assert.equal(old.$('upload-meter').hidden, false);
  assert.equal(old.$('vod-when-ready-row').hidden, true);
  assert.deepEqual(old.scripts, []);
  assert.deepEqual(old.pendingPolls(), [10000]);
  reply = finishedMeta();
  await old.advance(10000);
  assert.equal(old.state(), 'state-game');
  old.$('vod-play').click();
  await old.advance(0);
  assert.equal(old.video.src, playlist(1));

  const none = openPage({ browser: 'none', routes: { [META]: hlsMeta({ playable: false }) } });
  await none.advance(0);
  assert.equal(none.state(), 'state-game');
  assert.equal(none.message(), "This browser can't play shared games. Try a recent version of Chrome, Edge, Firefox or Safari.");
  assert.equal(none.$('vod-retry').hidden, true);
  assert.deepEqual(none.pendingPolls(), []);

  const { format, playable, media_seconds, ...mp4 } = finishedMeta();
  const plain = openPage({ routes: { [META]: { ...mp4, content_type: 'video/mp4', size_bytes: 1 } } });
  await plain.advance(0);
  assert.equal(plain.video.src, 'https://clips.revu.lol/vod-file/Abc1234');
  assert.equal(plain.video.preload, 'none', 'a play counts on Play');
  assert.equal(plain.video.controls, false, 'an MP4 share plays in the page transport too');
  assert.deepEqual(plain.scripts, []);
  assert.equal(plain.mints(), 0);
  assert.deepEqual([plain.$('vod-play').hidden, plain.$('vod-play-label').textContent], [false, 'Play'], 'a dark picture until then: Play over it');
  plain.$('vod-play').click();
  assert.deepEqual([plain.video.plays, plain.$('vod-play').hidden, plain.mints()], [1, true, 0]);
});

test('when the upload ends mid-play the page becomes a normal game page; an early end says how far it got', async () => {
  let reply = hlsMeta();
  const run = await playing({ routes: { [META]: () => reply } });
  run.mark('Baron at 15:20');
  reply = finishedMeta();
  const polls = run.metaPolls();
  run.hls().level(1000, false);
  await run.advance(0);
  assert.equal(run.metaPolls(), polls + 1, 'one /vod-meta to learn how it ended');
  assert.equal(run.lineText(), null, 'a game that uploaded to the end needs no line under it');
  assert.equal(run.$('vod-live-say').textContent, 'The whole game has uploaded.', 'screen readers hear it once');
  assert.equal(run.$('vod-lane-uploaded').hidden, true);
  assert.ok(run.marks().every(mark => mark.getAttribute('aria-pressed') === null && !mark.className.includes('is-pending')));
  await run.advance(10 * 60000);
  assert.equal(run.metaPolls(), polls + 1, 'nothing left to ask');

  reply = hlsMeta();
  const early = await playing({ routes: { [META]: () => reply } });
  reply = finishedMeta({ truncated: true, media_seconds: 610, duration_s: 610 });
  await early.advance(30000);
  assert.equal(early.lineText(), 'The sharer stopped before the end. Shared up to 10:10.');
  assert.ok(early.marks().every(mark => !mark.className.includes('is-pending')), 'a moment past the end is not on the lane');
  assert.deepEqual(early.pendingPolls(), []);

  // A game that ended early before the page opened says so; a complete one says nothing.
  const opened = openPage({ routes: { [META]: finishedMeta({ truncated: true, media_seconds: 610, duration_s: 610 }) } });
  await opened.advance(0);
  assert.equal(opened.lineText(), 'The sharer stopped before the end. Shared up to 10:10.');
});

test('a paused player in a hidden tab stops loading, and loads again when the tab is shown', async () => {
  const run = await playing({ routes: { [META]: hlsMeta() } });
  const hls = run.hls();
  run.video.pause();
  run.document.hidden = true;
  run.document.dispatch('visibilitychange');
  assert.deepEqual(hls.last(), ['stopLoad']);
  const polls = run.metaPolls();
  await run.advance(5 * 60000);
  assert.equal(run.metaPolls(), polls, 'a hidden tab never asks');
  run.document.hidden = false;
  run.document.dispatch('visibilitychange');
  await run.advance(0);
  assert.deepEqual(hls.last(), ['startLoad', 0]);
  assert.equal(run.metaPolls(), polls + 1);
});

test('the page has a Play button over the player, a quiet live line with a polite announcer, Try again and Play when ready', () => {
  assert.match(html, /<button id="vod-play" class="vod-play" type="button" hidden><svg class="vod-play-icon"[^>]*aria-hidden="true"[^>]*>[\s\S]*?<\/svg><span id="vod-play-label">Play from the start<\/span><\/button>/);
  assert.match(html, /<p id="vod-live" class="vod-live" hidden><span id="vod-live-text"><\/span><button id="vod-live-action" class="vod-live-action" type="button" hidden><\/button><span id="vod-live-after"><\/span><\/p>/);
  assert.match(html, /<p id="vod-live-say" class="visually-hidden" role="status" aria-live="polite"><\/p>/);
  assert.match(html, /<p id="vod-jump" class="vod-jump" hidden><span id="vod-jump-text"><\/span> <button id="vod-jump-cancel" class="vod-live-action" type="button">Cancel<\/button><\/p>/);
  assert.match(html, /<button id="vod-retry" class="btn-pill vod-retry" type="button" hidden>Try again<\/button>/);
  assert.match(html, /<span id="vod-lane-uploaded" class="vod-lane-uploaded" aria-hidden="true" hidden><\/span>/);
  assert.match(html, /<label id="vod-when-ready-row" class="vod-when-ready" hidden>\s*<input id="vod-when-ready" type="checkbox" \/>\s*<span>Play when ready<\/span>\s*<\/label>/);
  assert.match(html, /<video id="vod-video" class="vod-video" playsinline preload="none"><\/video>/);
  const scripts = Object.values(sources).join('\n');
  assert.doesNotMatch(html + scripts, DASHES, 'no dashes in copy or comments');
  assert.doesNotMatch(scripts, /innerHTML|insertAdjacentHTML|outerHTML|document\.write|\beval\(|new Function/);
  // Only review notes keep anything in the browser: the viewer's own notes, under revu.notes.*,
  // read and written through review-notes.js. Nothing names or follows the viewer.
  for (const [name, source] of Object.entries(sources)) {
    assert.doesNotMatch(source, /sessionStorage|document\.cookie|indexedDB/, name);
    if (name !== 'notes') assert.doesNotMatch(source, /localStorage/, `${name} leaves storage to review-notes.js`);
  }
  assert.deepEqual([...sources.notes.matchAll(/localStorage/g)].length, 1, 'one place reads the storage');
  assert.match(sources.notes, /var PREFIX = 'revu\.notes\.';/);
  for (const [name, source] of Object.entries(sources)) {
    assert.ok(source.split('\n').length <= 500, `${name} stays under 500 lines`);
  }
});
