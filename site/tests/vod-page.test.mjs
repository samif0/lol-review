// The shared full game page (vod.html at /v/<id>): id and proxy rules, meta states,
// the marker lane's game-to-video time mapping, notes as text only, and a page that
// stays unlisted with no video card.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const read = name => readFileSync(new URL(`../${name}`, import.meta.url), 'utf8');
const html = read('vod.html');
const script = read('vod.js');
const flush = async (rounds = 8) => { for (let i = 0; i < rounds; i += 1) await new Promise(resolve => setImmediate(resolve)); };
const flat = text => String(text).replace(/[  ]/g, ' ');

function helpers() {
  const context = { URLSearchParams };
  vm.runInNewContext(script, context);
  return context.RevuVod;
}

function element(document, tag, attributes = '') {
  const classes = new Set((attributes.match(/class="([^"]*)"/)?.[1] || '').split(/\s+/).filter(Boolean));
  const attrs = new Map(), listeners = new Map();
  const el = {
    tagName: tag.toUpperCase(), hidden: /\shidden\b/.test(` ${attributes}`), textContent: '', title: '', type: '',
    style: {}, children: [], parentNode: null,
    get className() { return [...classes].join(' '); },
    set className(value) { classes.clear(); String(value).split(/\s+/).filter(Boolean).forEach(c => classes.add(c)); },
    classList: { toggle: (c, on) => { if (on) classes.add(c); else classes.delete(c); }, contains: c => classes.has(c) },
    get firstChild() { return this.children[0] || null; },
    appendChild(child) { child.parentNode?.removeChild(child); child.parentNode = this; this.children.push(child); return child; },
    removeChild(child) { this.children = this.children.filter(c => c !== child); child.parentNode = null; return child; },
    setAttribute: (key, value) => attrs.set(key, String(value)),
    getAttribute: key => (attrs.has(key) ? attrs.get(key) : null),
    removeAttribute: key => attrs.delete(key),
    addEventListener(type, fn) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(fn); },
    dispatch(type) { for (const fn of listeners.get(type) || []) fn({ type, target: el }); },
    click() { el.dispatch('click'); },
  };
  if (tag === 'video') {
    Object.assign(el, { duration: NaN, currentTime: 0, src: '', plays: 0, paused: true,
      play() { el.plays += 1; el.paused = false; return Promise.resolve(); }, pause() { el.paused = true; }, load() {} });
  }
  return el;
}

function page({ pathname = '/v/Abc1234', search = '', hostname = 'revu.lol', routes = {}, now = Date.UTC(2026, 9, 14, 12) } = {}) {
  const docListeners = new Map();
  const document = { title: '', hidden: false, byId: new Map(), getElementById: id => document.byId.get(id) || null,
    createElement: tag => element(document, tag),
    addEventListener(type, fn) { if (!docListeners.has(type)) docListeners.set(type, []); docListeners.get(type).push(fn); },
    dispatch(type) { for (const fn of docListeners.get(type) || []) fn({ type }); } };
  for (const match of html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
    document.byId.set(match[3], element(document, match[1], match[2]));
  }
  // The lane's playhead starts inside the track, as in the page.
  document.byId.get('vod-lane-track').appendChild(document.byId.get('vod-lane-playhead'));
  const requests = [], timers = [], timeouts = [];
  const context = {
    document, URLSearchParams, location: { pathname, search, hostname },
    Date: class extends Date { static now() { return now; } },
    setInterval: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearInterval: () => {},
    setTimeout: (fn, ms) => { timeouts.push({ fn, ms, live: true }); return timeouts.length; },
    clearTimeout: handle => { if (timeouts[handle - 1]) timeouts[handle - 1].live = false; },
    fetch: async url => {
      requests.push(url);
      const path = url.replace(/^https?:\/\/[^/]+/, '');
      const route = routes[path];
      const reply = typeof route === 'function' ? route() : route;
      if (!reply) return { ok: false, status: 404, json: async () => ({ error: 'not_found' }) };
      // A reply is the JSON body, or { httpStatus, body } for an error answer.
      const status = typeof reply.httpStatus === 'number' ? reply.httpStatus : 200;
      return { ok: status < 300, status, json: async () => reply.body ?? reply };
    },
  };
  vm.runInNewContext(script, context);
  const $ = id => document.byId.get(id);
  // Runs the one pending timeout (the next poll), as the browser would after its delay.
  const nextPoll = async () => {
    const pending = timeouts.filter(t => t.live);
    assert.equal(pending.length, 1, 'exactly one poll is scheduled');
    pending[0].live = false;
    pending[0].fn();
    await flush();
    return pending[0].ms;
  };
  return { document, requests, timers, timeouts, nextPoll, $, video: $('vod-video'),
    pending: () => timeouts.filter(t => t.live),
    state: () => ['state-loading', 'state-missing', 'state-uploading', 'state-game'].find(id => !$(id).hidden) };
}

const NOW_S = Date.UTC(2026, 9, 14, 12) / 1000;
function meta(overrides = {}, doc = {}) {
  return {
    id: 'Abc1234', kind: 'vod', content_type: 'video/mp4', quality: '1080p', duration_s: 1840, size_bytes: 1315583333,
    created_at: NOW_S - 3600, expires_at: NOW_S + 3 * 86400 - 3600, serving: true, play_limited: false,
    meta: { version: 1, gameTimeAtVideoStart: 0, game: { champion: 'Jinx', opponentChampion: 'Caitlyn', role: 'BOTTOM',
      result: 'win', durationS: 1834, playedAt: '2026-10-12' },
    markers: [{ t: 312, kind: 'DRAGON', label: 'Dragon', tone: 'gold' }, { t: 920, kind: 'KILL', label: 'Kill', tone: 'win' }],
    notes: null, ...doc },
    ...overrides,
  };
}

test('ids come from /v/<id> or ?id=, and the proxy override is loopback-only on a local page', () => {
  const { parseId, apiBase } = helpers();
  assert.equal(parseId('/v/Abc1234', ''), 'Abc1234');
  assert.equal(parseId('/v/Abc1234/', ''), 'Abc1234');
  assert.equal(parseId('/vod', '?id=Abc1234'), 'Abc1234');
  assert.equal(parseId('/vod.html', '?id=%20Abc1234%20'), 'Abc1234');
  for (const [path, search] of [['/v/Abc123', ''], ['/v/Abc12345', ''], ['/v/../x', ''], ['/vod', '?id=../../x'], ['/vod', ''],
    ['/v/Abc1234/extra', '']]) {
    assert.equal(parseId(path, search), '', `${path}${search}`);
  }
  assert.equal(apiBase('revu.lol', '?proxy=http://localhost:8787'), 'https://clips.revu.lol');
  assert.equal(apiBase('localhost', '?proxy=http://localhost:8787/'), 'http://localhost:8787');
  assert.equal(apiBase('127.0.0.1', '?proxy=http://127.0.0.1:8787'), 'http://127.0.0.1:8787');
  for (const proxy of ['https://evil.example', 'http://evil.example:8787', 'http://localhost.evil.example', 'javascript:alert(1)']) {
    assert.equal(apiBase('localhost', `?proxy=${encodeURIComponent(proxy)}`), 'https://clips.revu.lol', proxy);
  }
});

test('header text, meta states and expiry copy', () => {
  const { titleOf, roleLabel, metaParts, metaState, expiryText } = helpers();
  assert.equal(titleOf({ champion: 'Jinx', opponentChampion: 'Caitlyn' }), 'Jinx vs Caitlyn');
  assert.equal(titleOf({ champion: 'Jinx' }), 'Jinx game');
  assert.equal(titleOf({}), 'Shared game');
  assert.equal(titleOf(null), 'Shared game');
  const roles = { TOP: 'Top', JUNGLE: 'Jungle', MIDDLE: 'Mid', MID: 'Mid', BOTTOM: 'Bot', ADC: 'Bot', UTILITY: 'Support',
    SUPPORT: 'Support', FILL: 'FILL' };
  for (const [role, label] of Object.entries(roles)) assert.equal(roleLabel(role), label, role);
  assert.deepEqual([...metaParts({ role: 'BOTTOM', result: 'win', durationS: 1834, playedAt: '2026-10-12' }, 'en-US')],
    ['Bot', 'Win', '30:34', 'Oct 12, 2026']);
  assert.deepEqual([...metaParts({ result: 'remake', durationS: 3725 }, 'en-US')], ['Remake', '1:02:05']);
  assert.deepEqual([...metaParts({ result: 'draw', playedAt: '12/10/2026' }, 'en-US')], []);
  assert.equal(metaState(meta()), 'ok');
  assert.equal(metaState(meta({ serving: false })), 'paused');
  assert.equal(metaState(meta({ play_limited: true })), 'limited');
  assert.equal(metaState(null), 'missing');
  const now = NOW_S * 1000;
  assert.equal(expiryText(NOW_S + 5 * 3600 + 59, now), 'This link expires in 5 hours.');
  assert.equal(expiryText(NOW_S + 3600, now), 'This link expires in 1 hour.');
  assert.equal(expiryText(NOW_S + 600, now), 'This link expires in less than an hour.');
  assert.equal(flat(expiryText(NOW_S + 2 * 86400, now, { locale: 'en-US', timeZone: 'UTC' })), 'This link expires Friday 12:00 PM.');
  assert.equal(expiryText(NOW_S, now), '');
});

test('the lane maps game time to video time with a negative or positive offset', () => {
  const { laneItems, noteRows } = helpers();
  const doc = offset => ({ gameTimeAtVideoStart: offset,
    markers: [{ t: 0, kind: 'START', label: 'Start' }, { t: 100, kind: 'DRAGON', label: 'Dragon', tone: 'gold' },
      { t: 950, kind: 'BARON', label: 'Baron', tone: 'bogus' }],
    notes: [{ t: 400, end: 430, kind: 'clip', text: 'Should have backed' }, { t: 655, kind: 'bookmark', text: '' }] });
  // Recording started 12 s before the game clock: game 100 s plays at video 112 s.
  const early = laneItems(doc(-12), 1000);
  assert.deepEqual([...early].map(item => [item.label, item.vt, Math.round(item.left * 10) / 10, item.tone, item.note]), [
    ['Start', 12, 1.2, '', false], ['Dragon', 112, 11.2, 'gold', false], ['Baron', 962, 96.2, '', false],
    ['Clip', 412, 41.2, 'note', true], ['Bookmark', 667, 66.7, 'note', true]]);
  // A moment past the end of the video (here game 1000 s at video 1012 s) is skipped.
  assert.equal(laneItems({ gameTimeAtVideoStart: -12, markers: [{ t: 1000, kind: 'END', label: 'End' }] }, 1000).length, 0);
  assert.equal(early.find(item => item.label === 'Dragon').aria, 'Dragon at 1:40');
  assert.equal(early.find(item => item.label === 'Clip').aria, 'Clip at 6:40');
  // Recording started 30 s into the game: game 100 s plays at video 70 s, and earlier moments clamp to 0.
  const late = laneItems(doc(30), 1000);
  assert.deepEqual([...late].map(item => [item.label, item.vt]), [['Start', 0], ['Dragon', 70], ['Baron', 920], ['Clip', 370], ['Bookmark', 625]]);
  assert.equal(late.find(item => item.label === 'Baron').tone, '', 'an unknown tone draws plain');
  assert.equal(laneItems(doc(0), 0).length, 0, 'no duration, no lane');
  assert.deepEqual([...noteRows(doc(30))].map(row => [row.time, row.kind, row.text, row.vt]),
    [['6:40', 'Clip', 'Should have backed', 370], ['10:55', 'Bookmark', '', 625]]);
  assert.deepEqual([...noteRows({ notes: null })], []);
});

test('unavailable, paused and play-limited games show their message and never load the video', async () => {
  const cases = [
    [{}, 'Game not available', "This shared game isn't available. Shared games stay up for 3 days."],
    [{ '/vod-meta/Abc1234': meta({ serving: false }) }, 'Playback paused', 'Playback is paused right now. Try again later.'],
    [{ '/vod-meta/Abc1234': meta({ play_limited: true }) }, 'Playback limit reached', 'This game has been watched a lot today. Try again tomorrow.'],
    [{ '/vod-meta/Abc1234': meta({ expires_at: NOW_S - 1 }) }, 'Game not available', "This shared game isn't available. Shared games stay up for 3 days."],
  ];
  for (const [routes, title, copy] of cases) {
    const run = page({ routes });
    await flush();
    assert.equal(run.state(), 'state-missing', title);
    assert.equal(run.$('state-title').textContent, title);
    assert.equal(run.$('state-copy').textContent, copy);
    assert.ok(!run.video.src, 'no video request');
  }
  const bad = page({ pathname: '/v/nope' });
  await flush();
  assert.equal(bad.state(), 'state-missing');
  assert.deepEqual(bad.requests, []);
});

test('a ready game renders its header, banner, lane and notes as text, and markers seek the video', async () => {
  const hostile = '<img src=x onerror=alert(1)>';
  const run = page({ routes: { '/vod-meta/Abc1234': meta({}, { gameTimeAtVideoStart: -12,
    notes: [{ t: 400, end: 430, kind: 'clip', text: hostile }, { t: 655, kind: 'bookmark', text: '' }] }) } });
  await flush();
  assert.deepEqual(run.requests, ['https://clips.revu.lol/vod-meta/Abc1234']);
  assert.equal(run.state(), 'state-game');
  assert.equal(run.video.src, 'https://clips.revu.lol/vod-file/Abc1234');
  assert.equal(run.$('vod-title').textContent, 'Jinx vs Caitlyn');
  assert.equal(run.document.title, 'Jinx vs Caitlyn - Revu');
  assert.match(run.$('vod-meta').textContent, /^Bot · Win · 30:34 · /);
  assert.equal(run.$('vod-quality').textContent, '1080p');
  assert.equal(run.$('vod-id').textContent, 'Abc1234');
  assert.equal(run.$('vod-expiry').hidden, false);
  assert.match(flat(run.$('vod-expiry').textContent), /^This link expires \w+day \d{1,2}:\d{2} [AP]M\.$/);
  assert.ok(run.timers.some(t => t.ms === 60000), 'the banner refreshes and notices expiry');

  // Before metadata, the lane uses duration_s (1840 s); after it, the real duration.
  const marks = () => run.$('vod-lane-track').children.filter(child => child.tagName === 'BUTTON');
  assert.equal(run.$('vod-lane').hidden, false);
  assert.equal(marks().length, 4);
  const dragon = marks().find(mark => mark.getAttribute('aria-label') === 'Dragon at 5:12');
  assert.equal(dragon.className, 'vod-mark is-gold');
  assert.equal(dragon.style.left, `${(324 / 1840) * 100}%`);
  run.video.duration = 2000;
  run.video.dispatch('loadedmetadata');
  const dragonAfter = marks().find(mark => mark.getAttribute('aria-label') === 'Dragon at 5:12');
  assert.equal(dragonAfter.style.left, `${(324 / 2000) * 100}%`);
  dragonAfter.click();
  assert.equal(run.video.currentTime, 324);
  assert.equal(run.video.plays, 1);
  assert.equal(marks().filter(mark => mark.className === 'vod-mark is-note').length, 2);

  assert.equal(run.$('vod-notes').hidden, false);
  assert.equal(run.$('state-game').classList.contains('has-notes'), true);
  const rows = run.$('vod-notes-list').children;
  assert.equal(rows.length, 2);
  const first = rows[0].children[0];
  assert.deepEqual(first.children.map(child => child.textContent), ['6:40', 'Clip', hostile]);
  assert.equal(first.children[2].children.length, 0, 'note text stays text');
  assert.deepEqual(rows[1].children[0].children.map(child => child.textContent), ['10:55', 'Bookmark']);
  first.click();
  assert.equal(run.video.currentTime, 412);
  run.video.currentTime = 1000;
  run.video.dispatch('timeupdate');
  assert.equal(run.$('vod-lane-playhead').style.left, '50%');
});

test('the quality pill shows the frame rate when the share has one, and nothing for an unknown value', async () => {
  for (const [quality, shown] of [['1080p60', '1080p60'], ['720p30', '720p30'], ['720p', '720p'], ['1080p144', ''], [null, '']]) {
    const run = page({ routes: { '/vod-meta/Abc1234': meta({ quality }) } });
    await flush();
    assert.equal(run.$('vod-quality').textContent, shown, String(quality));
    assert.equal(run.$('vod-quality').hidden, !shown);
  }
});

test('without notes or markers the lane and notes stay hidden; a playback error rechecks the meta', async () => {
  let current = meta({}, { markers: [], notes: [] });
  const run = page({ hostname: 'localhost', search: '?proxy=http://localhost:8787', routes: { '/vod-meta/Abc1234': () => current } });
  await flush();
  assert.equal(run.video.src, 'http://localhost:8787/vod-file/Abc1234');
  assert.equal(run.$('vod-lane').hidden, true);
  assert.equal(run.$('vod-notes').hidden, true);
  current = meta({ play_limited: true });
  run.video.dispatch('error');
  await flush();
  assert.equal(run.$('vod-player-msg').hidden, false);
  assert.equal(run.$('vod-player-msg').textContent, 'This game has been watched a lot today. Try again tomorrow.');
  assert.equal(run.$('state-game').classList.contains('is-blocked'), true);
  current = meta();
  run.video.dispatch('error');
  await flush();
  assert.equal(run.$('vod-player-msg').textContent, "This video couldn't be played.");
  current = null;
  run.video.dispatch('error');
  await flush();
  assert.equal(run.state(), 'state-missing');
});

// A share whose upload is still running: /vod-meta answers status "uploading".
function uploading(overrides = {}) {
  return { id: 'Abc1234', kind: 'vod', status: 'uploading', quality: '1080p60', duration_s: 1834, created_at: NOW_S - 60,
    expires_at: NOW_S + 3 * 86400 - 60, serving: true, progress: { parts: 0, part_count: 90 }, stalled: false, ...overrides };
}

test('upload progress is an estimate that never reads 100%, and polling slows down over time and when stalled', () => {
  const { metaState, uploadPercent, uploadText, pollDelay, UPLOADING_COPY, STALLED_COPY } = helpers();
  assert.equal(metaState(uploading()), 'uploading');
  assert.equal(metaState(uploading({ serving: false })), 'paused', 'a pause wins: the game could not play anyway');
  for (const [progress, percent] of [[{ parts: 0, part_count: 90 }, 0], [{ parts: 30, part_count: 90 }, 33],
    [{ parts: 89, part_count: 90 }, 98], [{ parts: 90, part_count: 90 }, 99], [{ parts: 95, part_count: 90 }, 99],
    [{ parts: 'x', part_count: 90 }, 0], [{ parts: 3, part_count: 0 }, 0], [null, 0]]) {
    assert.equal(uploadPercent({ progress }), percent, JSON.stringify(progress));
  }
  assert.deepEqual({ ...uploadText(uploading({ progress: { parts: 30, part_count: 90 } })) },
    { title: 'Still uploading, 33%', copy: UPLOADING_COPY });
  assert.deepEqual({ ...uploadText(uploading({ progress: { parts: 60, part_count: 90 }, stalled: true })) },
    { title: 'Upload paused at 66%', copy: STALLED_COPY });
  assert.equal(UPLOADING_COPY, 'This game plays here as soon as the upload finishes. You can leave this page open.');
  assert.match(html, new RegExp(`<p id="upload-copy"[^>]*>${UPLOADING_COPY}</p>`), 'the page starts with the same copy');
  assert.deepEqual([pollDelay(0, false), pollDelay(59999, false), pollDelay(60000, false), pollDelay(0, true)],
    [4000, 4000, 10000, 30000]);
});

test('a game that is still uploading shows its progress, keeps asking, and starts the player once it is ready', async () => {
  const { UPLOADING_COPY, STALLED_COPY } = helpers();
  let current = uploading({ progress: { parts: 30, part_count: 90 } });
  const run = page({ routes: { '/vod-meta/Abc1234': () => (typeof current === 'function' ? current() : current) } });
  await flush();
  assert.equal(run.state(), 'state-uploading');
  assert.equal(run.$('upload-title').textContent, 'Still uploading, 33%');
  assert.equal(run.$('upload-copy').textContent, UPLOADING_COPY);
  assert.equal(run.$('upload-bar').style.width, '33%');
  assert.equal(run.$('upload-meter').getAttribute('aria-valuenow'), '33');
  assert.equal(run.document.title, 'Still uploading, 33% - Revu');
  assert.ok(!run.video.src, 'nothing to play yet');
  assert.deepEqual(run.requests, ['https://clips.revu.lol/vod-meta/Abc1234']);

  current = uploading({ progress: { parts: 60, part_count: 90 } });
  assert.equal(await run.nextPoll(), 4000);
  assert.equal(run.$('upload-title').textContent, 'Still uploading, 66%');
  assert.equal(run.requests.length, 2);

  // Stalled: it says so, and asks less often.
  current = uploading({ progress: { parts: 60, part_count: 90 }, stalled: true });
  await run.nextPoll();
  assert.equal(run.$('upload-title').textContent, 'Upload paused at 66%');
  assert.equal(run.$('upload-copy').textContent, STALLED_COPY);
  assert.equal(run.pending()[0].ms, 30000);

  // A server error or a dropped connection is not the end: it asks again.
  current = { httpStatus: 503, body: { error: 'clip_error' } };
  await run.nextPoll();
  current = () => { throw new TypeError('Failed to fetch'); };
  await run.nextPoll();
  assert.equal(run.state(), 'state-uploading');
  assert.equal(run.$('upload-title').textContent, 'Upload paused at 66%');
  assert.equal(run.pending().length, 1);

  // Ready: the normal game page, with the video loaded and nothing left to ask.
  current = meta();
  await run.nextPoll();
  assert.equal(run.state(), 'state-game');
  assert.equal(run.video.src, 'https://clips.revu.lol/vod-file/Abc1234');
  assert.equal(run.$('vod-title').textContent, 'Jinx vs Caitlyn');
  assert.equal(run.document.title, 'Jinx vs Caitlyn - Revu');
  assert.equal(run.pending().length, 0, 'polling stops');
  assert.equal(run.requests.length, 6);
});

test('while it uploads, the page names the game once the sharer has sent its details, as text only', async () => {
  const { uploadGame } = helpers();
  const doc = meta().meta;
  assert.equal(uploadGame(uploading()), null);
  assert.equal(uploadGame(uploading({ meta: null })), null);
  assert.deepEqual({ ...uploadGame(uploading({ meta: doc }), 'en-US') },
    { title: 'Jinx vs Caitlyn', meta: 'Bot · Win · 30:34 · Oct 12, 2026' });
  assert.deepEqual({ ...uploadGame(uploading({ meta: { ...doc, game: { champion: 'Jinx' } } })) }, { title: 'Jinx game', meta: '' });
  assert.deepEqual({ ...uploadGame(uploading({ meta: { ...doc, game: { role: 'MIDDLE' } } })) }, { title: 'Shared game', meta: 'Mid' });
  for (const game of [{}, null, 'Jinx', { champion: '  ' }]) {
    assert.equal(uploadGame(uploading({ meta: { ...doc, game } })), null, JSON.stringify(game));
  }

  const hostile = '<img src=x onerror=alert(1)>';
  let current = uploading({ progress: { parts: 30, part_count: 90 } });
  const run = page({ routes: { '/vod-meta/Abc1234': () => current } });
  await flush();
  assert.equal(run.state(), 'state-uploading');
  assert.equal(run.$('upload-game').hidden, true, 'nothing to name before the details arrive');

  current = uploading({ progress: { parts: 31, part_count: 90 }, meta: { ...doc, game: { ...doc.game, opponentChampion: hostile } } });
  await run.nextPoll();
  assert.equal(run.$('upload-game').hidden, false);
  assert.equal(run.$('upload-game-title').textContent, `Jinx vs ${hostile}`);
  assert.equal(run.$('upload-game-title').children.length, 0, 'the name stays text');
  assert.match(run.$('upload-game-meta').textContent, /^Bot · Win · 30:34 · /);
  assert.equal(run.$('upload-game-meta').hidden, false);
  // The rest of the waiting page is unchanged.
  assert.equal(run.$('upload-title').textContent, 'Still uploading, 34%');
  assert.equal(run.$('upload-bar').style.width, '34%');
  assert.equal(run.document.title, 'Still uploading, 34% - Revu');
  assert.ok(!run.video.src, 'nothing to play yet');

  current = uploading({ progress: { parts: 31, part_count: 90 }, stalled: true, meta: { ...doc, game: { champion: 'Jinx' } } });
  await run.nextPoll();
  assert.equal(run.$('upload-title').textContent, 'Upload paused at 34%');
  assert.equal(run.$('upload-game-title').textContent, 'Jinx game');
  assert.equal(run.$('upload-game-meta').hidden, true);

  current = meta();
  await run.nextPoll();
  assert.equal(run.state(), 'state-game');
  assert.equal(run.$('vod-title').textContent, 'Jinx vs Caitlyn');
  assert.equal(run.video.src, 'https://clips.revu.lol/vod-file/Abc1234');
});

test('an upload that ends while someone waits says the game is not available; a hidden tab waits to ask', async () => {
  let current = uploading();
  const run = page({ routes: { '/vod-meta/Abc1234': () => current } });
  await flush();
  assert.equal(run.$('upload-title').textContent, 'Still uploading, 0%');
  run.document.hidden = true;
  await run.nextPoll();
  assert.equal(run.requests.length, 1, 'a hidden tab does not ask');
  assert.equal(run.pending().length, 0);
  run.document.hidden = false;
  run.document.dispatch('visibilitychange');
  await flush();
  assert.equal(run.requests.length, 2, 'it asks as soon as it is shown');
  run.document.dispatch('visibilitychange');
  await flush();
  assert.equal(run.requests.length, 2, 'and only once');
  current = null;
  await run.nextPoll();
  assert.equal(run.state(), 'state-missing');
  assert.equal(run.$('state-title').textContent, 'Game not available');
  assert.equal(run.pending().length, 0);

  const paused = page({ routes: { '/vod-meta/Abc1234': uploading({ serving: false }) } });
  await flush();
  assert.equal(paused.state(), 'state-missing');
  assert.equal(paused.$('state-title').textContent, 'Playback paused');
  assert.equal(paused.pending().length, 0);
});

test('the page is unlisted, has no video card or inline script, and offers a report contact', () => {
  assert.match(html, /<meta name="robots" content="noindex, nofollow" \/>/);
  assert.match(html, /<meta property="og:title" content="Shared League game - Revu" \/>/);
  assert.match(html, /<meta property="og:description" content="Watch a League of Legends game shared with Revu\." \/>/);
  assert.match(html, /<meta property="og:image" content="https:\/\/revu\.lol\/og-image\.jpg" \/>/);
  assert.doesNotMatch(html, /og:video|twitter:player|twitter:card/);
  for (const asset of ['/tokens.css', '/styles.css', '/vod.css']) assert.match(html, new RegExp(`<link rel="stylesheet" href="${asset}" />`));
  assert.match(html, /<script src="\/vod\.js" defer><\/script>/);
  assert.doesNotMatch(html, /<script\b(?![^>]*\bsrc=)[^>]*>/);
  for (const tag of html.match(/<[^>]+>/g)) assert.doesNotMatch(tag, /\son[a-z]+\s*=/i, tag);
  assert.doesNotMatch(html, /(?:href|src)="(?!\/|https:\/\/)[^"]*"/, 'every local path is absolute (the page is served at /v/<id>)');
  const text = html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
  assert.match(text, /Only people with the link can see this page\./);
  assert.match(text, /To report this video, contact us on Discord \. Video ID/);
  assert.match(html, /<a href="\/discord">Discord<\/a>/);
  assert.match(html, /<video id="vod-video" class="vod-video" controls playsinline preload="metadata"><\/video>/);
  assert.doesNotMatch(html + script, /[–—]/);
  assert.doesNotMatch(html, /@/, 'no email address on the page');
  assert.doesNotMatch(script, /innerHTML|insertAdjacentHTML|outerHTML|document\.write/);
});
