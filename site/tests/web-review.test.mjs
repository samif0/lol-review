// The web review tool at /review (review.html + app.js +
// web-review-lib.js): per-match notes, Turnstile timeout and accessibility,
// error copy, the region list, the match card markup and the scrubber.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import { flush, memoryStorage } from './fake-dom.mjs';

const read = name => readFileSync(new URL(`../${name}`, import.meta.url), 'utf8');
const libSource = read('web-review-lib.js');
const appSource = read('app.js');
const reviewHtml = read('review.html');

function lib() {
  const context = {};
  vm.runInNewContext(libSource, context);
  return context.RevuWebReview;
}

// ── A small DOM stand-in: every selector gets one stable fake element ─────────

function makeEl(tag, focused) {
  const listeners = {};
  const attrs = new Map();
  const classes = new Set();
  const subs = new Map();
  const el = {
    tag,
    hidden: false,
    disabled: false,
    value: '',
    textContent: '',
    innerHTML: '',
    type: '',
    className: '',
    dataset: {},
    style: {},
    children: [],
    options: [],
    duration: 0,
    currentTime: 0,
    paused: true,
    classList: {
      add: (...v) => v.forEach(x => classes.add(x)),
      remove: (...v) => v.forEach(x => classes.delete(x)),
      toggle: (v, on) => ((on ?? !classes.has(v)) ? classes.add(v) : classes.delete(v)),
      contains: v => classes.has(v),
    },
    setAttribute: (k, v) => attrs.set(k, String(v)),
    getAttribute: k => (attrs.has(k) ? attrs.get(k) : null),
    removeAttribute: k => attrs.delete(k),
    addEventListener: (t, f) => (listeners[t] ||= []).push(f),
    removeEventListener: (t, f) => { listeners[t] = (listeners[t] || []).filter(x => x !== f); },
    dispatch(type, extra = {}) {
      const event = { type, target: el, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, stopPropagation() {}, ...extra };
      (listeners[type] || []).forEach(f => f(event));
      return event;
    },
    append: (...c) => el.children.push(...c),
    querySelector(sel) {
      if (!subs.has(sel)) subs.set(sel, makeEl(sel, focused));
      return subs.get(sel);
    },
    querySelectorAll: () => [],
    focus() { focused.push(el); },
    closest: () => null,
    load() {},
    play() {},
    pause() {},
    remove() {},
    click() { el.dispatch('click'); },
    requestSubmit() { el.dispatch('submit'); },
    setPointerCapture() {},
    getBoundingClientRect: () => ({ left: 0, width: 100 }),
    requestFullscreen: async () => {},
  };
  return el;
}

class FakeURL extends URL {
  static blobs = [];
  static createObjectURL(blob) { FakeURL.blobs.push(blob); return 'blob:revu'; }
  static revokeObjectURL() {}
}

const REGION_OPTIONS = [...reviewHtml.matchAll(/<option value="([^"]+)">([^<]+)<\/option>/g)].map(m => ({ value: m[1], textContent: m[2] }));

function app({ storage = memoryStorage(), routes = {}, turnstile } = {}) {
  const focused = [];
  const elements = new Map();
  const created = [];
  const timers = [];
  const requests = [];
  const document = {
    body: makeEl('body', focused),
    fullscreenElement: null,
    querySelector(sel) {
      if (!elements.has(sel)) {
        const el = makeEl(sel, focused);
        if (sel === '#region') el.options = REGION_OPTIONS;
        elements.set(sel, el);
      }
      return elements.get(sel);
    },
    createElement(tag) {
      const el = makeEl(tag, focused);
      created.push(el);
      return el;
    },
    addEventListener() {},
  };
  const context = {
    document,
    localStorage: storage,
    sessionStorage: memoryStorage(),
    location: { hostname: 'revu.lol' },
    turnstile,
    URL: FakeURL,
    Blob,
    console,
    performance: { now: () => 0 },
    setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; },
    clearTimeout: id => { if (timers[id - 1]) timers[id - 1].cleared = true; },
    setInterval: () => 0,
    clearInterval: () => {},
    fetch: async (url, init = {}) => {
      const path = String(url).replace(/^https?:\/\/[^/]+/, '').split('?')[0];
      const key = `${init.method || 'GET'} ${path}`;
      requests.push(key);
      const route = routes[key];
      if (route instanceof Error) throw route;
      if (typeof route === 'function') return route();
      if (route) return route;
      if (path === '/api/versions.json') return { ok: true, status: 200, json: async () => ['16.11.1'] };
      return { ok: false, status: 404, json: async () => ({}) };
    },
  };
  context.window = context;
  vm.createContext(context);
  vm.runInContext(libSource, context);
  vm.runInContext(appSource, context);
  const $ = sel => document.querySelector(sel);
  return { context, document, $, storage, timers, requests, focused, created };
}

const ok = body => ({ ok: true, status: 200, headers: { get: () => null }, json: async () => body });
const status = (code, body = {}) => ({ ok: false, status: code, headers: { get: () => null }, json: async () => body });

function openMatch(run, matchId) {
  run.$('#match-list').dispatch('click', { target: { closest: () => ({ dataset: { matchId } }) } });
}

function turnstileStub({ autoSolve = false } = {}) {
  return {
    renders: [],
    removed: 0,
    render(container, options) { this.options = options; this.renders.push(options); return 'w1'; },
    execute() { if (autoSolve) this.options.callback('turnstile-token'); },
    remove() { this.removed += 1; },
  };
}

async function search(run, riotId = 'Name#TAG') {
  run.$('#riot-id').value = riotId;
  run.$('#player-form').dispatch('submit');
  await flush();
}

// ── Notes ─────────────────────────────────────────────────────────────────────

test('notesFor/saveNotes keep notes per match and prune to the newest 200', () => {
  const { notesFor, saveNotes, NOTES_KEY } = lib();
  const store = memoryStorage();
  saveNotes(store, 'NA1_1', 'first game', 1);
  saveNotes(store, 'NA1_2', 'second game', 2);
  assert.equal(notesFor(store, 'NA1_1'), 'first game');
  assert.equal(notesFor(store, 'NA1_2'), 'second game');
  assert.equal(notesFor(store, 'NA1_3'), '');
  saveNotes(store, 'NA1_1', '', 3);
  assert.equal(notesFor(store, 'NA1_1'), '');
  for (let i = 0; i < 205; i += 1) saveNotes(store, `EUW1_${i}`, `n${i}`, 10 + i);
  const kept = Object.keys(JSON.parse(store.getItem(NOTES_KEY)));
  assert.equal(kept.length, 200);
  assert.ok(kept.includes('EUW1_204'));
  assert.ok(!kept.includes('NA1_2'), 'the oldest entries go first');
  assert.equal(notesFor(null, 'x'), '');
  const broken = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); } };
  assert.equal(notesFor(broken, 'NA1_1'), '');
  assert.doesNotThrow(() => saveNotes(broken, 'NA1_1', 'x', 1));
});

test('opening another match shows its own notes, never the previous match\'s', async () => {
  const run = app();
  openMatch(run, 'NA1_1234567890');
  assert.equal(run.$('#review-notes').value, '');
  run.$('#review-notes').value = 'lost lane to a level 2 all-in';
  run.$('#review-notes').dispatch('input');

  openMatch(run, 'NA1_1234567871');
  assert.equal(run.$('#review-notes').value, '', 'game B starts empty');
  run.$('#review-notes').value = 'good baron call';
  run.$('#review-notes').dispatch('input');

  openMatch(run, 'NA1_1234567890');
  assert.equal(run.$('#review-notes').value, 'lost lane to a level 2 all-in');

  FakeURL.blobs.length = 0;
  run.$('#export-review').dispatch('click');
  const markdown = await FakeURL.blobs[0].text();
  assert.match(markdown, /- Match: NA1_1234567890/);
  assert.match(markdown, /lost lane to a level 2 all-in/);
  assert.doesNotMatch(markdown, /good baron call/);
});

test('the old single global note moves onto the match it was last shown with', () => {
  const storage = memoryStorage({
    'revu.web.simple.v1': JSON.stringify({ riotId: 'A#B', region: 'euw1', notes: 'old global note', selectedMatchId: 'EUW1_42' }),
  });
  const run = app({ storage });
  assert.equal(lib().notesFor(storage, 'EUW1_42'), 'old global note');
  assert.equal(JSON.parse(storage.getItem('revu.web.simple.v1')).notes, undefined);
  assert.equal(run.$('#region').value, 'euw1');
});

// ── Turnstile ─────────────────────────────────────────────────────────────────

test('an unanswered Turnstile challenge times out after 60 s instead of hanging', async () => {
  const turnstile = turnstileStub();
  const run = app({ turnstile });
  await search(run);
  assert.equal(run.$('#search-status').textContent, 'Looking up Riot ID...');
  assert.equal(run.$('#player-form').querySelector('button[type=submit]').disabled, true);
  const timeout = run.timers.find(t => t.ms === 60_000 && !t.cleared);
  assert.ok(timeout, 'a 60 s timeout is armed');
  timeout.fn();
  await flush();
  assert.equal(run.$('#search-status').textContent, "We couldn't verify your browser. Try again.");
  assert.equal(run.$('#player-form').querySelector('button[type=submit]').disabled, false);
  assert.equal(turnstile.removed, 1, 'the stale widget is removed');
});

test('Turnstile timeout-callback and error-callback settle the search too', async () => {
  for (const name of ['timeout-callback', 'error-callback', 'expired-callback']) {
    const turnstile = turnstileStub();
    const run = app({ turnstile });
    await search(run);
    turnstile.options[name]();
    await flush();
    assert.equal(run.$('#search-status').textContent, "We couldn't verify your browser. Try again.", name);
  }
});

test('an interactive challenge is announced, exposed to assistive tech and focused', async () => {
  const turnstile = turnstileStub();
  const run = app({ turnstile });
  const container = run.$('#turnstile-container');
  await search(run);
  turnstile.options['before-interactive-callback']();
  assert.equal(container.classList.contains('is-interactive'), true);
  assert.equal(container.getAttribute('aria-hidden'), null);
  assert.equal(run.$('#search-status').textContent, 'Complete the quick security check to continue.');
  assert.equal(run.focused.at(-1), container);
  turnstile.options.callback('tok');
  await flush();
  assert.equal(container.classList.contains('is-interactive'), false);
  assert.equal(container.getAttribute('aria-hidden'), 'true');
});

test('the Turnstile container is aria-hidden only while it is offscreen', () => {
  assert.match(reviewHtml, /<div id="turnstile-container" role="region" aria-label="Security check" tabindex="-1" aria-hidden="true"><\/div>/);
  assert.match(reviewHtml, /id="search-status" role="status" aria-live="polite"/);
});

// ── Copy ──────────────────────────────────────────────────────────────────────

test('lookup failures get user copy that tells Turnstile, network and Riot apart', async () => {
  const cases = [
    [{ 'POST /web/session': new TypeError('Failed to fetch') }, "Couldn't reach Revu. Check your connection and try again."],
    [{ 'POST /web/session': status(429) }, 'Too many lookups from your network. Wait a few minutes and try again.'],
    [{ 'POST /web/session': status(403) }, "Couldn't start a lookup. Try again in a moment."],
    [{ 'POST /web/session': ok({ token: 't', expiresIn: 1800 }), 'GET /web/account': status(403) }, 'Riot lookups are unavailable right now. Try again later.'],
    [{ 'POST /web/session': ok({ token: 't', expiresIn: 1800 }), 'GET /web/account': status(429) }, 'Too many lookups right now. Wait a few seconds and try again.'],
    [{ 'POST /web/session': ok({ token: 't', expiresIn: 1800 }), 'GET /web/account': status(504) }, 'Riot took too long to answer. Try again.'],
    [{ 'POST /web/session': ok({ token: 't', expiresIn: 1800 }), 'GET /web/account': status(404) }, 'No account found for Name#TAG in NA.'],
  ];
  for (const [routes, copy] of cases) {
    const run = app({ routes, turnstile: turnstileStub({ autoSolve: true }) });
    await search(run);
    assert.equal(run.$('#search-status').textContent, copy);
  }
});

test('no user-facing copy mentions the proxy, wrangler or an em dash', () => {
  const { errorCopy } = lib();
  for (const key of ['turnstile', 'session_busy', 'session', 'network', 'expired', 'busy', 'timeout', 'riot', 'unknown']) {
    assert.doesNotMatch(errorCopy(key), /proxy|wrangler|[–—]/i);
  }
  assert.doesNotMatch(appSource, /Is wrangler dev running|Could not reach proxy/);
});

test('the list is titled as recent games and the page says Revu is a desktop app', async () => {
  const routes = {
    'POST /web/session': ok({ token: 't', expiresIn: 1800 }),
    'GET /web/account': ok({ puuid: 'p', gameName: 'Faker', tagLine: 'KR1' }),
    'GET /web/matches': ok([]),
  };
  const run = app({ routes, turnstile: turnstileStub({ autoSolve: true }) });
  await search(run, 'Faker#KR1');
  assert.equal(run.$('#matches-title').textContent, "Faker#KR1's recent games");
  assert.match(reviewHtml, /Revu is a Windows desktop app for reviewing your League games\./);
  assert.match(reviewHtml, /Type a Riot ID to pull your recent games\./);
  assert.doesNotMatch(reviewHtml, /recent ranked games/i);
});

test('the region list offers exactly the platforms the proxy accepts', () => {
  const riot = readFileSync(new URL('../../proxy/src/riot.ts', import.meta.url), 'utf8');
  const block = riot.match(/PLATFORM_TO_REGIONAL[^{]*\{([\s\S]*?)\};/)[1];
  const platforms = [...block.matchAll(/^\s*([a-z0-9]+):\s*"/gm)].map(m => m[1]).sort();
  assert.equal(platforms.length, 17);
  assert.deepEqual(REGION_OPTIONS.map(o => o.value).sort(), platforms);
});

// ── Markup and the scrubber ───────────────────────────────────────────────────

test('match cards are buttons holding only phrasing content', async () => {
  const run = app();
  // Show the demo list.
  run.$('#back-to-search').dispatch('click');
  openMatch(run, 'NA1_1234567890');
  run.$('#back-to-matches').dispatch('click');
  const tags = new Set(run.created.map(el => el.tag));
  assert.ok(tags.has('button'));
  assert.ok(!tags.has('div') && !tags.has('p'), `card parts: ${[...tags].join(', ')}`);
});

test('moving to another screen focuses its heading', () => {
  const run = app();
  openMatch(run, 'NA1_1234567890');
  const heading = run.$('#review-screen').querySelector('h1');
  assert.equal(run.focused.at(-1), heading);
  assert.equal(heading.getAttribute('tabindex'), '-1');
});

test('the scrubber is a keyboard and touch operable slider', () => {
  assert.match(reviewHtml, /id="vod-track" role="slider" tabindex="0" aria-label="Seek video"/);
  const run = app();
  const video = run.$('#video-player');
  const track = run.$('#vod-track');
  video.duration = 100;
  video.currentTime = 50;
  track.dispatch('keydown', { key: 'Home' });
  assert.equal(video.currentTime, 0);
  track.dispatch('keydown', { key: 'End' });
  assert.equal(video.currentTime, 100);
  video.currentTime = 50;
  track.dispatch('keydown', { key: 'PageDown' });
  assert.equal(video.currentTime, 40);
  // Pointer events (mouse, pen or touch): 10px rail inset on a 100px track.
  track.dispatch('pointerdown', { clientX: 50, button: 0, pointerId: 1 });
  assert.equal(video.currentTime, 50);
  track.dispatch('pointermove', { clientX: 90 });
  assert.equal(video.currentTime, 100);
  track.dispatch('pointerup', {});
  track.dispatch('pointermove', { clientX: 10 });
  assert.equal(video.currentTime, 100, 'the drag ends on pointerup');
  video.dispatch('timeupdate');
  assert.equal(track.getAttribute('aria-valuenow'), '100');
  assert.equal(track.getAttribute('aria-valuetext'), '01:40 of 01:40');
});
