// Coach tools on the shared clip page (clip-coach.js): the pen with D and the Draw button, and
// review notes (B, I and O) at the clip's own clock. clip.html's inline player script is pinned by
// hash in _headers, so these tools only add to it: their keys run first and stop there.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const read = name => readFileSync(new URL(`../${name}`, import.meta.url), 'utf8');
const html = read('clip.html');
const flush = async (rounds = 8) => { for (let i = 0; i < rounds; i += 1) await new Promise(resolve => setImmediate(resolve)); };

function element(tag, attributes = '') {
  const classes = new Set((attributes.match(/class="([^"]*)"/)?.[1] || '').split(/\s+/).filter(Boolean));
  const attrs = new Map(), listeners = new Map();
  const el = {
    tagName: tag.toUpperCase(), hidden: /\shidden\b/.test(` ${attributes}`), textContent: '', value: '', title: '', type: '',
    style: {}, children: [], parentNode: null,
    get className() { return [...classes].join(' '); },
    set className(value) { classes.clear(); String(value).split(/\s+/).filter(Boolean).forEach(c => classes.add(c)); },
    classList: { toggle: (c, on) => { if (on) classes.add(c); else classes.delete(c); }, contains: c => classes.has(c),
      add: c => classes.add(c), remove: c => classes.delete(c) },
    get firstChild() { return el.children[0] || null; },
    appendChild(child) { child.parentNode?.removeChild(child); child.parentNode = el; el.children.push(child); return child; },
    removeChild(child) { el.children = el.children.filter(c => c !== child); child.parentNode = null; return child; },
    setAttribute: (key, value) => attrs.set(key, String(value)),
    getAttribute: key => (attrs.has(key) ? attrs.get(key) : null),
    removeAttribute: key => attrs.delete(key),
    addEventListener(type, fn) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(fn); },
    dispatch(type) {
      const event = { type, target: el, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
      for (const fn of [...(listeners.get(type) || [])]) fn(event);
      return event;
    },
    click() { el.dispatch('click'); },
    focus() { el.page.focused = el; },
    blur() { if (el.page.focused === el) el.page.focused = null; },
    select() {},
  };
  if (tag === 'video') {
    Object.assign(el, { duration: NaN, currentTime: 0, paused: true,
      play() { el.paused = false; return Promise.resolve(); }, pause() { el.paused = true; } });
  }
  return el;
}

function memoryStorage() {
  const map = new Map();
  return { map, get length() { return map.size; }, key: i => [...map.keys()][i] ?? null, getItem: k => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => { map.set(k, String(v)); }, removeItem: k => { map.delete(k); } };
}

// A stand-in for vod-ink.js: it records what reaches it. While it is out, C clears and Escape
// puts it away, as the real pen does.
function fakeInk() {
  const seen = { keys: [], toggles: 0 };
  const Ink = { attach(o) {
    let active = false;
    seen.stage = o.stage;
    const set = on => { active = on; o.onChange({ active, hasInk: false }); };
    return {
      toggle() { seen.toggles += 1; set(!active); },
      active: () => active,
      key(ev) {
        if (!active) return false;
        seen.keys.push(ev.key);
        if (ev.key === 'Escape') { set(false); return true; }
        return ev.key === 'c' || ev.key === '[';
      },
    };
  } };
  return { Ink, seen };
}

function clipPage({ search = '?id=Clip001', hash = '', storage = memoryStorage(), ink = fakeInk() } = {}) {
  const page = { focused: null };
  const windowListeners = [], playerKeys = [], player = { listener: null };
  const byId = new Map();
  let wrap = null;
  for (const match of html.matchAll(/<([a-z][\w-]*)\b([^>]*)>/g)) {
    const [, tag, attributes] = match;
    const id = attributes.match(/\sid="([^"]+)"/)?.[1];
    const isWrap = /class="clip-video-wrap"/.test(attributes);
    if (!id && !isWrap) continue;
    const el = element(tag, attributes);
    el.page = page;
    if (id) byId.set(id, el);
    if (isWrap) wrap = el;
  }
  const make = tag => { const el = element(tag); el.page = page; return el; };
  const document = {
    getElementById: id => byId.get(id) || null,
    querySelector: selector => (selector === '.clip-video-wrap' ? wrap : null),
    createElement: make,
    createElementNS: (ns, tag) => make(tag),
    // clip.html's own player listens here; it must never see a key the coach tools used.
    addEventListener(type, fn) { if (type === 'keydown') player.listener = fn; },
  };
  const context = {
    document, URLSearchParams, TextEncoder, TextDecoder, btoa, atob, localStorage: storage, RevuVodInk: ink.Ink,
    location: { search, pathname: '/clip', href: `https://revu.lol/clip${search}${hash}` },
    history: { state: null, replaced: [], replaceState(state, title, url) { context.history.replaced.push(url); } },
    navigator: {}, setTimeout: () => 0, clearTimeout: () => {},
    addEventListener(type, fn, capture) { if (type === 'keydown') windowListeners.push({ fn, capture }); },
  };
  vm.createContext(context);
  for (const name of ['review-notes.js', 'review-notes-ui.js', 'clip-coach.js']) vm.runInContext(read(name), context);
  document.addEventListener('keydown', ev => playerKeys.push(ev.key));
  const $ = id => byId.get(id);
  $('state-clip').hidden = false;
  // A key press: the window's capture listeners first, then, unless one stopped it, the player's.
  const press = (key, init = {}) => {
    const event = { type: 'keydown', key, target: { tagName: 'BODY' }, repeat: false, shiftKey: false, ctrlKey: false, metaKey: false,
      altKey: false, defaultPrevented: false, stopped: false, ...init,
      preventDefault() { this.defaultPrevented = true; }, stopPropagation() { this.stopped = true; } };
    for (const { fn, capture } of windowListeners) if (capture && !event.stopped) fn(event);
    if (!event.stopped) player.listener(event);
    return event;
  };
  const rows = () => $('clip-mine-list').children.map(li => li.children[0].children.map(c => c.textContent));
  return { $, page, press, rows, playerKeys, storage, context, ink: ink.seen, video: $('clip-video') };
}

test('the clip page loads the pen and the notes after its player, and shows the keys: Draw D, Full F', () => {
  const scripts = [...html.matchAll(/<script src="\/([\w.-]+)" defer><\/script>/g)].map(m => m[1]);
  assert.deepEqual(scripts, ['review-notes.js', 'review-notes-ui.js', 'vod-ink.js', 'clip-coach.js']);
  for (const sheet of ['/review-notes.css', '/vod-ink.css', '/clip-coach.css']) assert.match(html, new RegExp(`<link rel="stylesheet" href="${sheet}" />`));
  assert.match(html, /<button id="clip-draw" class="clip-icon-button" type="button" aria-pressed="false" aria-label="Draw on the video" title="Draw on the video \(D\)" hidden>Draw <kbd aria-hidden="true">D<\/kbd><\/button>/);
  assert.match(html, /<button id="clip-fullscreen" class="clip-icon-button" type="button" aria-label="Fullscreen" title="Full screen \(F\)">Full <kbd aria-hidden="true">F<\/kbd><\/button>/);
  assert.match(html, /<section id="clip-notes" class="clip-side-panel clip-notes" aria-labelledby="clip-notes-title" hidden>/);
});

test('the pen: D or Draw puts it out; while it is out its keys stop there and never reach the player', () => {
  const run = clipPage();
  assert.equal(run.$('clip-draw').hidden, false, 'Draw shows once the pen is attached');
  assert.equal(run.ink.stage.className, 'clip-video-wrap', 'the pen covers the picture');
  run.$('clip-draw').click();
  assert.deepEqual([run.$('clip-draw').getAttribute('aria-pressed'), run.ink.stage.classList.contains('is-drawing')], ['true', true]);
  const c = run.press('c');
  assert.deepEqual([c.stopped, c.defaultPrevented, run.playerKeys], [true, true, []], 'C clears the ink and does not turn the captions');
  run.press('[');
  assert.deepEqual(run.playerKeys, [], '[ changes the line, not the speed');
  run.press('k');
  assert.deepEqual(run.playerKeys, ['k'], 'Space and K still play and pause');
  run.press('Escape');
  assert.equal(run.$('clip-draw').getAttribute('aria-pressed'), 'false');
  run.press('c');
  assert.deepEqual(run.playerKeys, ['k', 'c'], 'with the pen away, C is the captions again');
  const d = run.press('d');
  assert.deepEqual([d.stopped, run.ink.toggles, run.$('clip-draw').getAttribute('aria-pressed')], [true, 2, 'true']);
  run.press('c', { target: run.$('clip-composer-text') });
  assert.deepEqual(run.ink.keys.filter(k => k === 'c').length, 1, 'typing in a note never reaches the pen');
});

test('notes on a clip: B and I then O at the clip\'s clock, diamonds on the track, and a clip that plays to its end', async () => {
  const run = clipPage();
  await flush();
  assert.equal(run.$('clip-notes').hidden, false);
  run.video.currentTime = 3;
  run.press('b');
  assert.equal(run.$('clip-composer-when').textContent, 'Bookmark at 0:03.');
  assert.equal(run.page.focused, run.$('clip-composer-text'));
  run.$('clip-composer-text').value = 'Watch their support';
  const enter = run.press('Enter', { target: run.$('clip-composer-text') });
  assert.equal(enter.stopped, true, 'Enter saved the note, so the player never sees it');
  run.video.currentTime = 8;
  run.press('i');
  run.video.duration = 40;
  run.video.dispatch('loadedmetadata');
  run.video.currentTime = 15;
  run.video.dispatch('timeupdate');
  const band = run.$('clip-track-range');
  assert.deepEqual([band.hidden, band.style.left, band.style.width], [false, 'calc(10px + (100% - 20px) * 0.2)', 'calc((100% - 20px) * 0.175)']);
  run.press('o');
  run.press('Enter', { target: run.$('clip-composer-text') });
  assert.deepEqual(run.rows(), [['0:03', 'Bookmark', 'Watch their support'], ['0:08', 'Clip', '7s']]);
  assert.deepEqual(run.$('clip-track-marks').children.map(m => [m.className, m.style.left]),
    [['clip-track-mark', '7.5%'], ['clip-track-mark is-clip', '20%']]);
  assert.equal(JSON.parse(run.storage.getItem('revu.notes.c.Clip001')).notes.length, 2);
  assert.deepEqual(run.playerKeys, [], 'B, I, O and Enter were the notes\'');

  run.$('clip-mine-list').children[1].children[0].click();
  assert.deepEqual([run.video.currentTime, run.video.paused], [8, false]);
  run.video.currentTime = 15.2;
  run.video.dispatch('timeupdate');
  assert.equal(run.video.paused, true, 'the clip ends where it was made to');

  run.$('clip-mine-list').children[0].children[1].click();
  assert.deepEqual(run.rows(), [['0:08', 'Clip', '7s']]);
  run.$('clip-notes-status').children[1].click();
  assert.deepEqual(run.rows().length, 2, 'Undo brings it back');
});

test('a clip link with notes adds them; without a clip id there are no notes, and the pen still works', async () => {
  const payload = 'j' + Buffer.from(JSON.stringify([[2, 'Flash forward here']])).toString('base64url');
  const run = clipPage({ hash: `#notes=${payload}` });
  await flush();
  assert.deepEqual(run.rows(), [['0:02', 'Bookmark', 'Flash forward here']]);
  assert.equal(run.$('clip-notes-status').children[0].textContent, 'Added 1 note from the link.');
  assert.deepEqual(run.context.history.replaced, ['https://revu.lol/clip?id=Clip001']);

  const bare = clipPage({ search: '' });
  await flush();
  assert.equal(bare.$('clip-notes').hidden, true);
  assert.equal(bare.$('clip-draw').hidden, false);
  bare.press('b');
  assert.deepEqual(bare.playerKeys, ['b'], 'no notes: B is left alone');
});
