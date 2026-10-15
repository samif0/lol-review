// A fake browser for the shared game page (vod.html and its scripts): the DOM from the
// page's HTML, a video element with an autoplay policy, a fake hls.js that records every call,
// timers on a fake clock, and a fake proxy. Not a test suite itself (tests/index.js only loads
// *.test.mjs).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

export const read = name => readFileSync(new URL(`../${name}`, import.meta.url), 'utf8');
export const html = read('vod.html');
// ink and controls (the canvas and the reviewer transport) are checked for their rules here but not
// run: this DOM has no canvas or SVG, and the page works without them.
export const sources = { lib: read('vod-lib.js'), ink: read('vod-ink.js'), controls: read('vod-controls.js'),
  player: read('vod-player.js'), live: read('vod-live.js'), page: read('vod.js') };
// The tests swap the real pin (empty until hls.js is vendored) for this one; the fake loader checks it.
export const TEST_INTEGRITY = `sha384-${'A'.repeat(64)}`;
const PIN = /var HLS_INTEGRITY = '[^']*';/;

export const flush = async (rounds = 12) => { for (let i = 0; i < rounds; i += 1) await new Promise(resolve => setImmediate(resolve)); };
// En and em dashes, and the no-break spaces ICU puts in times ("2:03 PM"), built from code
// points so these files stay plain ASCII.
export const DASHES = new RegExp(`[${String.fromCharCode(0x2013, 0x2014)}]`);
export const SPACES = new RegExp(`[${String.fromCharCode(0x202f, 0xa0)}]`, 'g');

// What each kind of browser reports. Chromium says "maybe" to native HLS but must never use it.
export const BROWSERS = {
  chrome: { mse: true, mms: false, native: 'maybe', policy: 'sticky' },
  firefox: { mse: true, mms: false, native: '', policy: 'sticky' },
  safari: { mse: true, mms: true, native: 'maybe', policy: 'gesture' },
  iphone: { mse: false, mms: true, native: 'maybe', policy: 'gesture' },
  oldIos: { mse: false, mms: false, native: 'maybe', policy: 'gesture' },
  none: { mse: false, mms: false, native: '', policy: 'sticky' },
};

export const NOW = Date.UTC(2026, 9, 14, 12);
export const NOW_S = NOW / 1000;
export const token = n => `${NOW_S + 43200}.${`tok${n}`.padEnd(22, 'x')}`;
export const playlist = n => `https://clips.revu.lol/vod-hls/Abc1234/${token(n)}/index.m3u8`;

export const DOC = { version: 1, gameTimeAtVideoStart: 0,
  game: { champion: 'Jinx', opponentChampion: 'Caitlyn', role: 'BOTTOM', result: 'win', durationS: 1000, playedAt: '2026-10-12' },
  markers: [{ t: 30, kind: 'KILL', label: 'Kill', tone: 'win' }, { t: 312, kind: 'DRAGON', label: 'Dragon', tone: 'gold' },
    { t: 920, kind: 'BARON', label: 'Baron', tone: 'gold' }],
  notes: [{ t: 400, kind: 'clip', text: 'Should have backed' }] };

// An HLS share's /vod-meta reply (uploading and playable unless overridden), as the contract has it.
export function hlsMeta(overrides = {}) {
  return { id: 'Abc1234', kind: 'vod', format: 'hls', status: 'uploading', quality: '1080p60', duration_s: 1000,
    created_at: NOW_S - 60, expires_at: NOW_S + 3 * 86400 - 60, now: NOW_S, serving: true, play_limited: false,
    playable: true, media_seconds: 120, ingest_x: 9, last_commit_age_s: 1, paused_reason: null, ended: false, truncated: false,
    progress: { parts: 60, part_count: 512 }, meta: DOC, ...overrides };
}

export function finishedMeta(overrides = {}) {
  const { status, ...rest } = hlsMeta({ media_seconds: 1000, ended: true, ingest_x: null, last_commit_age_s: 5, ...overrides });
  return rest;
}

function makeElement(win, tag, attributes = '') {
  const classes = new Set((attributes.match(/class="([^"]*)"/)?.[1] || '').split(/\s+/).filter(Boolean));
  const attrs = new Map(), listeners = new Map();
  for (const m of attributes.matchAll(/\s(id|type|role|aria-[\w-]+)="([^"]*)"/g)) attrs.set(m[1], m[2]);
  const el = {
    tagName: tag.toUpperCase(), hidden: /\shidden\b/.test(` ${attributes}`), textContent: '', title: '', style: {},
    type: attrs.get('type') || '', checked: false, children: [], parentNode: null,
    get className() { return [...classes].join(' '); },
    set className(value) { classes.clear(); String(value).split(/\s+/).filter(Boolean).forEach(c => classes.add(c)); },
    classList: { toggle: (c, on) => { if (on) classes.add(c); else classes.delete(c); }, contains: c => classes.has(c),
      add: c => classes.add(c), remove: c => classes.delete(c) },
    get firstChild() { return this.children[0] || null; },
    appendChild(child) { child.parentNode?.removeChild(child); child.parentNode = this; this.children.push(child); return child; },
    removeChild(child) { this.children = this.children.filter(c => c !== child); child.parentNode = null; return child; },
    setAttribute(key, value) { attrs.set(key, String(value)); if (key === 'src') el.src = String(value); },
    getAttribute: key => (attrs.has(key) ? attrs.get(key) : null),
    removeAttribute(key) { attrs.delete(key); if (key === 'src') el.src = ''; },
    addEventListener(type, fn) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(fn); },
    removeEventListener(type, fn) { listeners.set(type, (listeners.get(type) || []).filter(f => f !== fn)); },
    dispatch(type) {
      const event = { type, target: el, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
      for (const fn of [...(listeners.get(type) || [])]) fn(event);
      return event;
    },
    // A press: a user gesture while the handlers run (Safari's rule) and sticky activation after (Chromium's).
    click() {
      if (el.type === 'checkbox') el.checked = !el.checked;
      win.activated = true;
      win.gesture = true;
      try { el.dispatch('click'); if (el.type === 'checkbox') el.dispatch('change'); } finally { win.gesture = false; }
    },
    focus() { win.focused = el; },
  };
  if (tag === 'video') makeVideo(win, el, attributes);
  return el;
}

function makeVideo(win, el, attributes) {
  let ranges = [];
  Object.assign(el, {
    duration: NaN, currentTime: 0, src: '', paused: true, ended: false, plays: 0, refused: 0, loads: 0,
    controls: /\scontrols\b/.test(` ${attributes}`), preload: attributes.match(/preload="([^"]*)"/)?.[1] || '',
    buffered: { get length() { return ranges.length; }, start: i => ranges[i][0], end: i => ranges[i][1] },
    setBuffered(list) { ranges = list; },
    canPlayType: type => (type === 'application/vnd.apple.mpegurl' ? win.caps.native : ''),
    // Chromium and Firefox: allowed after any press on the page. Safari: during a press, or on
    // an element a press has already played.
    play() {
      const allowed = win.caps.policy === 'sticky' ? win.activated : win.gesture || el.unlocked;
      if (!allowed) {
        el.refused += 1;
        const error = new Error('play() needs a press');
        error.name = 'NotAllowedError';
        return Promise.reject(error);
      }
      if (win.gesture) el.unlocked = true;
      el.plays += 1;
      const was = el.paused;
      el.paused = false;
      if (was) el.dispatch('play');
      return Promise.resolve();
    },
    pause() { if (el.paused) return; el.paused = true; el.dispatch('pause'); },
    load() { el.loads += 1; },
  });
}

// The parts of hls.js 1.7.3 the page uses, recording every call.
export function fakeHlsClass(win) {
  class FakeHls {
    static supported = true;
    static instances = [];
    static isSupported() { return FakeHls.supported; }
    constructor(config) { this.config = config; this.calls = []; this.handlers = new Map(); FakeHls.instances.push(this); }
    on(event, fn) { if (!this.handlers.has(event)) this.handlers.set(event, []); this.handlers.get(event).push(fn); }
    emit(event, data) { for (const fn of this.handlers.get(event) || []) fn(event, data); }
    attachMedia(media) {
      this.calls.push(['attachMedia']);
      this.media = media;
      // hls.js 1.7.3 (buffer-controller): ManagedMediaSource, preferred where it exists, will not
      // open unless disableRemotePlayback is set, so hls.js sets it on the element itself.
      if (win.caps.mms) media.disableRemotePlayback = media.disableRemotePlayback || true;
      media.src = 'blob:https://revu.lol/media-source';
    }
    // The manifest arrives a moment later, as over the network.
    loadSource(url) { this.calls.push(['loadSource', url]); setImmediate(() => { if (!this.destroyed) this.emit(FakeHls.Events.MANIFEST_PARSED, { levels: [{}] }); }); }
    startLoad(at) { this.calls.push(['startLoad', at]); }
    stopLoad() { this.calls.push(['stopLoad']); }
    recoverMediaError() { this.calls.push(['recoverMediaError']); }
    destroy() { this.calls.push(['destroy']); this.destroyed = true; }
    level(edge, live = true) { this.emit(FakeHls.Events.LEVEL_UPDATED, { level: 0, details: { edge, totalduration: edge, live, targetduration: 6 } }); }
    fail(type, fatal, code, details = 'fragLoadError') {
      this.emit(FakeHls.Events.ERROR, { type, details, fatal, response: code === undefined ? undefined : { code, text: '' } });
    }
    last() { return this.calls[this.calls.length - 1]; }
  }
  FakeHls.Events = { MANIFEST_PARSED: 'hlsManifestParsed', LEVEL_UPDATED: 'hlsLevelUpdated', ERROR: 'hlsError' };
  FakeHls.ErrorTypes = { NETWORK_ERROR: 'networkError', MEDIA_ERROR: 'mediaError', OTHER_ERROR: 'otherError' };
  return FakeHls;
}

function response(status, body) {
  return { ok: status >= 200 && status < 300, status, json: async () => body };
}

/*
 * Opens the page. routes: { 'GET /vod-meta/Abc1234': reply | fn, 'POST /vod-play/Abc1234': ... }.
 * A reply is a JSON body, { httpStatus, body } for an error answer, or an Error for a network
 * failure. POST /vod-play answers a fresh token each time unless a route says otherwise.
 * globals: names defined before the scripts run (a test's stand-ins for vod-ink.js and
 * vod-controls.js).
 */
export function openPage({ routes = {}, browser = 'chrome', pinned = true, hlsLoads = true, pathname = '/v/Abc1234', globals = {} } = {}) {
  let now = NOW;
  let mints = 0;
  const win = { caps: BROWSERS[browser], activated: false, gesture: false, focused: null };
  const docListeners = new Map(), winListeners = new Map();
  const scripts = [], requests = [], timers = [];
  const FakeHls = fakeHlsClass(win);
  const document = {
    title: '', hidden: false, byId: new Map(), getElementById: id => document.byId.get(id) || null,
    createElement: tag => makeElement(win, tag),
    head: { appendChild(el) {
      scripts.push(el);
      setImmediate(() => {
        if (hlsLoads && el.integrity === TEST_INTEGRITY) { context.Hls = FakeHls; el.onload(); } else el.onerror();
      });
      return el;
    } },
    addEventListener(type, fn) { if (!docListeners.has(type)) docListeners.set(type, []); docListeners.get(type).push(fn); },
    dispatch(type) { for (const fn of docListeners.get(type) || []) fn({ type }); },
  };
  for (const match of html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
    document.byId.set(match[3], makeElement(win, match[1], match[2]));
  }
  document.byId.get('vod-lane-track').appendChild(document.byId.get('vod-lane-uploaded'));
  document.byId.get('vod-lane-track').appendChild(document.byId.get('vod-lane-playhead'));
  const source = ok => (ok ? { isTypeSupported: type => /avc1\.64002a/.test(type) } : undefined);
  const schedule = (fn, ms, every) => { timers.push({ fn, at: now + ms, every, live: true }); return timers.length; };
  const cancel = handle => { if (timers[handle - 1]) timers[handle - 1].live = false; };
  const context = {
    document, URLSearchParams, location: { pathname, search: '', hostname: 'revu.lol' }, navigator: { onLine: true },
    MediaSource: source(win.caps.mse), ManagedMediaSource: source(win.caps.mms),
    Date: class extends Date { constructor(...args) { super(...(args.length ? args : [now])); } static now() { return now; } },
    setTimeout: (fn, ms) => schedule(fn, ms, 0), clearTimeout: cancel,
    setInterval: (fn, ms) => schedule(fn, ms, ms), clearInterval: cancel,
    addEventListener(type, fn) { if (!winListeners.has(type)) winListeners.set(type, []); winListeners.get(type).push(fn); },
    fetch: async (url, init = {}) => {
      const method = (init.method || 'GET').toUpperCase();
      const path = url.replace(/^https?:\/\/[^/]+/, '');
      requests.push({ method, path, url, init });
      let route = routes[`${method} ${path}`];
      if (route === undefined && method === 'POST' && path === '/vod-play/Abc1234') {
        route = () => { mints += 1; return { token: token(mints), expires_at: NOW_S + 43200 }; };
      }
      const reply = typeof route === 'function' ? route({ now, method, path }) : route;
      if (reply instanceof Error) throw reply;
      if (reply === undefined || reply === null) return response(404, { error: 'not_found' });
      if (typeof reply.httpStatus === 'number') return response(reply.httpStatus, reply.body ?? {});
      return response(200, reply);
    },
    ...globals,
  };
  vm.createContext(context);
  assert.equal(sources.player.match(new RegExp(PIN.source, 'g'))?.length, 1, 'vod-player.js has one HLS_INTEGRITY pin');
  const player = sources.player.replace(PIN, `var HLS_INTEGRITY = '${pinned ? TEST_INTEGRITY : ''}';`);
  for (const code of [sources.lib, player, sources.live, sources.page]) vm.runInContext(code, context);
  const $ = id => document.byId.get(id);

  // Moves the fake clock on, running every timer that comes due, in order.
  const advance = async ms => {
    const end = now + ms;
    await flush();
    for (;;) {
      const due = timers.filter(t => t.live && t.at <= end).sort((a, b) => a.at - b.at)[0];
      if (!due) break;
      now = due.at;
      if (due.every) due.at += due.every; else due.live = false;
      due.fn();
      await flush();
    }
    now = end;
    await flush();
  };
  const video = $('vod-video');
  // Playback moves on: currentTime, then the timeupdate the browser would fire.
  const playTo = t => { video.currentTime = t; video.dispatch('timeupdate'); };
  const marks = () => $('vod-lane-track').children.filter(child => child.tagName === 'BUTTON');
  const mark = label => marks().find(m => m.getAttribute('aria-label').startsWith(label));
  const lineText = () => ($('vod-live').hidden ? null
    : [$('vod-live-text'), $('vod-live-action'), $('vod-live-after')].filter(el => !el.hidden).map(el => el.textContent).join(''));
  const hls = () => FakeHls.instances[FakeHls.instances.length - 1];
  const count = (method, prefix) => requests.filter(r => r.method === method && r.path.startsWith(prefix)).length;
  return {
    document, context, requests, scripts, timers, FakeHls, video, $, advance, playTo, marks, mark, lineText, hls, count, win,
    now: () => now,
    metaPolls: () => count('GET', '/vod-meta/'),
    mints: () => count('POST', '/vod-play/'),
    pendingPolls: () => timers.filter(t => t.live && !t.every).map(t => t.at - now),
    online(on) { context.navigator.onLine = on; if (on) for (const fn of winListeners.get('online') || []) fn({ type: 'online' }); },
    // The window changes size and the lane is now `width` px wide.
    resize(width) { $('vod-lane-track').clientWidth = width; for (const fn of winListeners.get('resize') || []) fn({ type: 'resize' }); },
    state: () => ['state-loading', 'state-missing', 'state-uploading', 'state-game'].find(id => !$(id).hidden),
    message: () => ($('vod-player-msg').hidden ? null : $('vod-player-msg').textContent),
  };
}
