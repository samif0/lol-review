import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import {
  APPEARANCE_EVENT, APPEARANCE_KEY, DAYPARTS, DEFAULT_MODE, DEFAULT_THEME, FONT_PRELOADS, MODES, THEMES,
  appearanceOverride, daypartFor, pageFromPath, parseAppearance, readAppearance, readAppliedAppearance,
  resolveMode, saveAppearance, UNSAVED_ATTRIBUTE,
} from '../ui/appearance.mjs';

// nav-prepaint.js runs in a vm over fake DOM objects, the same way shell-navigation
// runs framed.js. It must agree with ui/appearance.mjs, which Settings and tests use.
const PREPAINT = await readFile(new URL('../ui/nav-prepaint.js', import.meta.url), 'utf8');
// Objects made inside the vm have that realm's prototypes; compare them as plain data.
const plain = value => JSON.parse(JSON.stringify(value));

function fakeElement(tagName = 'html') {
  const attrs = new Map(), classes = [], children = [];
  return {
    tagName, attrs, classes, children, style: {},
    classList: { add: name => classes.push(name) },
    setAttribute(name, value) { attrs.set(name, String(value)); },
    getAttribute(name) { return attrs.has(name) ? attrs.get(name) : null; },
    removeAttribute(name) { attrs.delete(name); },
    appendChild(node) { children.push(node); return node; },
  };
}
function memoryStorage(initial) {
  const data = new Map(initial === undefined ? [] : [[APPEARANCE_KEY, initial]]), writes = [];
  return { data, writes, getItem: key => (data.has(key) ? data.get(key) : null),
    setItem: (key, value) => { writes.push([key, value]); data.set(key, String(value)); } };
}
const blockedStorage = () => ({ getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); } });
function fakeMedia(light) {
  const listeners = [], query = { matches: light, addEventListener: (type, fn) => listeners.push([type, fn]) };
  const matchMedia = text => { matchMedia.queries.push(text); return query; };
  matchMedia.queries = [];
  matchMedia.flip = value => { query.matches = value; for (const [, fn] of listeners) fn({ matches: value }); };
  matchMedia.listeners = listeners;
  return matchMedia;
}
/** A top document that only exposes <html> attributes (or throws like a cross-origin top). */
function topWith(attributes, { crossOrigin = false } = {}) {
  if (crossOrigin) return { get document() { throw new Error('cross-origin'); } };
  return { document: { documentElement: { getAttribute: name => attributes[name] ?? null } } };
}

function runPrepaint({ pathname = '/dashboard.html', search = '', hash = '', stored, storage, light = false,
  matchMedia, noMatchMedia = false, hour = 14, top = null } = {}) {
  const root = fakeElement('html'), head = fakeElement('head');
  const clock = { hour }, holder = { frame: null };
  const events = [], listeners = {}, intervals = [];
  const win = {
    localStorage: storage ?? memoryStorage(stored),
    addEventListener: (type, fn) => (listeners[type] ||= []).push(fn),
    dispatchEvent: event => { events.push(event); return true; },
    CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init?.detail; } },
    setInterval: (fn, ms) => { intervals.push({ fn, ms }); return intervals.length; },
  };
  if (!noMatchMedia) win.matchMedia = matchMedia ?? fakeMedia(light);
  win.self = win;
  win.top = top ?? win;
  const document = {
    documentElement: root, head,
    createElement: tag => fakeElement(tag),
    getElementById: id => (id === 'app-frame' ? holder.frame : null),
  };
  win.document = document;
  class Clock extends Date { getHours() { return clock.hour; } }
  vm.runInContext(PREPAINT, vm.createContext({ window: win, document, location: { pathname, search, hash }, Date: Clock }));
  const attr = name => root.getAttribute(name);
  return {
    root, head, win, events, listeners, intervals, clock, api: win.__revuAppearance,
    shown: () => ({ theme: attr('data-theme'), mode: attr('data-mode'), pref: attr('data-mode-pref'), daypart: attr('data-daypart') }),
    preloads: () => head.children.map(link => link.getAttribute('href')),
    setFrame: frameWin => { holder.frame = { contentWindow: frameWin }; },
    fire: (type, event) => (listeners[type] || []).forEach(fn => fn(event)),
  };
}

const STORED_CASES = [undefined, '', 'not json', '{', 'null', '5', '[]', '"warm"', '{}',
  '{"theme":"constructor","mode":"__proto__"}', '{"theme":["aura"],"mode":"light"}', '{"theme":"AURA","mode":"Light"}',
  '{"v":2,"theme":"aura","mode":"system"}', '{"theme":"neutral"}', '{"mode":"light"}',
  ...THEMES.flatMap(theme => MODES.map(mode => JSON.stringify({ v: 1, theme, mode })))];

test('defaults are Warm and dark, applied before paint with the color scheme and the body faces preloaded', () => {
  const page = runPrepaint();
  assert.equal(DEFAULT_THEME, 'warm');
  assert.equal(DEFAULT_MODE, 'dark');
  assert.deepEqual(page.shown(), { theme: 'warm', mode: 'dark', pref: 'dark', daypart: 'day' });
  assert.equal(page.root.getAttribute('data-page'), 'dashboard');
  assert.equal(page.root.style.colorScheme, 'dark');
  assert.deepEqual(page.root.classes, []);
  assert.deepEqual(page.preloads(), ['./fonts/instrument-sans.woff2', './fonts/source-serif-4-roman.woff2']);
  for (const link of page.head.children) {
    assert.equal(link.tagName, 'link');
    assert.deepEqual(['rel', 'as', 'type', 'crossorigin'].map(name => link.getAttribute(name)), ['preload', 'font', 'font/woff2', '']);
  }
  assert.deepEqual(page.events, [], 'the first paint dispatches nothing');
  assert.equal(typeof page.api.get, 'function');
  assert.equal(typeof page.api.reapply, 'function');
  assert.deepEqual(plain(page.api.get()), { theme: 'warm', mode: 'dark', pref: 'dark', daypart: 'day', page: 'dashboard' });
});

test('the prepaint agrees with appearance.mjs over stored values, system preference and storage failures', () => {
  for (const stored of STORED_CASES) {
    for (const light of [false, true]) {
      const expected = parseAppearance(stored ?? null);
      const page = runPrepaint({ stored, light });
      assert.deepEqual(page.shown(), { theme: expected.theme, pref: expected.mode,
        mode: resolveMode(expected.mode, { prefersLight: light }), daypart: daypartFor(14) }, `${stored} light=${light}`);
      assert.equal(page.root.style.colorScheme, page.shown().mode);
      assert.deepEqual(readAppearance(memoryStorage(stored)), expected);
    }
  }
  assert.deepEqual(runPrepaint({ storage: blockedStorage(), light: true }).shown(),
    { theme: 'warm', mode: 'dark', pref: 'dark', daypart: 'day' });
  assert.deepEqual(readAppearance(blockedStorage()), { theme: DEFAULT_THEME, mode: DEFAULT_MODE });
  const system = JSON.stringify({ v: 1, theme: 'journal', mode: 'system' });
  assert.equal(runPrepaint({ stored: system, noMatchMedia: true }).shown().mode, 'dark', 'no matchMedia means dark');
  assert.equal(runPrepaint({ stored: system, matchMedia: () => { throw new Error('no'); } }).shown().mode, 'dark');
  assert.equal(runPrepaint({ stored: system, light: true }).shown().mode, 'light');
});

test('data-page is the file name without .html and the shell is index under any URL form', () => {
  const cases = { '/': 'index', '': 'index', '/index.html': 'index', '/index': 'index', '/ui/': 'index',
    '/dashboard': 'dashboard', '/ui/settings.html': 'settings', '/Review.HTML': 'review', '/vodplayer.html': 'vodplayer' };
  for (const [pathname, page] of Object.entries(cases)) {
    const run = runPrepaint({ pathname });
    assert.equal(run.root.getAttribute('data-page'), page, pathname);
    assert.equal(pageFromPath(pathname), page, pathname);
    assert.deepEqual(run.root.classes, page === 'index' ? ['app-root'] : [], pathname);
  }
});

test('a framed page mirrors the shell, and System follows the shell before Windows', () => {
  const stored = JSON.stringify({ v: 1, theme: 'neutral', mode: 'dark' });
  const shell = { 'data-theme': 'aura', 'data-mode': 'light', 'data-mode-pref': 'system', 'data-daypart': 'morning' };
  const framed = runPrepaint({ stored, hour: 22, top: topWith(shell) });
  assert.deepEqual(framed.shown(), { theme: 'aura', mode: 'light', pref: 'system', daypart: 'morning' });
  assert.equal(framed.root.style.colorScheme, 'light');
  assert.deepEqual(framed.preloads(), ['./fonts/instrument-sans.woff2', './fonts/source-serif-4-roman.woff2']);
  // A shell without a theme yet: storage decides, and System resolves from the shell's mode.
  const system = JSON.stringify({ v: 1, theme: 'journal', mode: 'system' });
  assert.deepEqual(runPrepaint({ stored: system, light: false, top: topWith({ 'data-mode': 'light' }) }).shown(),
    { theme: 'journal', mode: 'light', pref: 'system', daypart: 'day' });
  assert.equal(resolveMode('system', { prefersLight: false, topMode: 'light' }), 'light');
  assert.equal(resolveMode('system', { prefersLight: true, topMode: 'nonsense' }), 'light');
  // A cross-origin top is read from storage instead.
  assert.deepEqual(runPrepaint({ stored, top: topWith(shell, { crossOrigin: true }) }).shown(),
    { theme: 'neutral', mode: 'dark', pref: 'dark', daypart: 'day' });
  // Bad shell attributes fall back to sensible values.
  assert.deepEqual(runPrepaint({ hour: 3, top: topWith({ 'data-theme': 'journal', 'data-mode': 'x', 'data-daypart': 'noon' }) }).shown(),
    { theme: 'journal', mode: 'dark', pref: 'dark', daypart: 'night' });
});

test('?appearance=<theme>-<mode> overrides storage for previews and is never saved', () => {
  const stored = JSON.stringify({ v: 1, theme: 'neutral', mode: 'dark' });
  const page = runPrepaint({ stored, search: '?gameId=5&appearance=aura-light' });
  assert.deepEqual(page.shown(), { theme: 'aura', mode: 'light', pref: 'light', daypart: 'day' });
  assert.deepEqual(page.win.localStorage.writes, []);
  // The shell reads it from a deep-link hash, because clean URLs drop the shell's query.
  const shell = runPrepaint({ pathname: '/', hash: '#dashboard?appearance=journal-system', light: true });
  assert.deepEqual(shell.shown(), { theme: 'journal', mode: 'light', pref: 'system', daypart: 'day' });
  // A frame's own override wins over mirroring the shell.
  const framed = runPrepaint({ search: '?appearance=neutral-light', top: topWith({ 'data-theme': 'aura', 'data-mode': 'dark' }) });
  assert.deepEqual(framed.shown(), { theme: 'neutral', mode: 'light', pref: 'light', daypart: 'day' });
  for (const search of ['?appearance=sepia-light', '?appearance=aura-dim', '?appearance=aura', '?appearance=aura-lightx', '?xappearance=aura-light']) {
    assert.deepEqual(runPrepaint({ stored, search }).shown().theme, 'neutral', search);
    assert.equal(appearanceOverride(search), null, search);
  }
  assert.deepEqual(appearanceOverride('?appearance=AURA-Light'), { theme: 'aura', mode: 'light' });
  assert.deepEqual(appearanceOverride('#vodplayer.html?gameId=4&appearance=warm-system#x'), { theme: 'warm', mode: 'system' });
  assert.equal(appearanceOverride(undefined), null);
});

test('reapply applies a choice, dispatches only on change and the shell forwards it to the frame', () => {
  const shell = runPrepaint({ pathname: '/index.html', stored: JSON.stringify({ v: 1, theme: 'warm', mode: 'dark' }) });
  const frame = runPrepaint({ top: shell.win });
  assert.deepEqual(frame.shown(), shell.shown(), 'the frame mirrors the shell at load');
  shell.setFrame(frame.win);
  const result = plain(shell.api.reapply({ theme: 'neutral', mode: 'light' }));
  assert.deepEqual(result, { theme: 'neutral', mode: 'light', pref: 'light', daypart: 'day', page: 'index' });
  assert.deepEqual(shell.shown(), { theme: 'neutral', mode: 'light', pref: 'light', daypart: 'day' });
  assert.deepEqual(frame.shown(), shell.shown(), 'forwarded to the frame');
  assert.equal(shell.root.style.colorScheme, 'light');
  assert.equal(shell.events.length, 1);
  assert.equal(shell.events[0].type, APPEARANCE_EVENT);
  assert.deepEqual(plain(shell.events[0].detail), result);
  assert.equal(frame.events.length, 1);
  assert.equal(frame.events[0].detail.page, 'dashboard');
  assert.ok(shell.preloads().includes('./fonts/geist.woff2'), 'the new theme preloads its face');
  shell.api.reapply({ theme: 'neutral', mode: 'light' });
  assert.equal(shell.events.length, 1, 'no event without a change');
  assert.equal(frame.events.length, 1);
  // System is resolved by the shell and the frame follows the shell's mode.
  shell.api.reapply({ theme: 'aura', mode: 'system' });
  assert.deepEqual(frame.shown(), { theme: 'aura', mode: 'dark', pref: 'system', daypart: 'day' });
  // A held choice survives the shell's own refreshes (a failed save keeps its look).
  shell.intervals[0].fn();
  assert.equal(shell.shown().theme, 'aura');
  // reapply() with no choice reads storage again.
  shell.api.reapply();
  assert.deepEqual(shell.shown(), { theme: 'warm', mode: 'dark', pref: 'dark', daypart: 'day' });
  assert.deepEqual(frame.shown(), shell.shown());
  const invalid = runPrepaint();
  invalid.api.reapply({ theme: 'sepia', mode: 'dim' });
  assert.deepEqual(invalid.shown(), { theme: 'warm', mode: 'dark', pref: 'dark', daypart: 'day' });
  assert.equal(invalid.events.length, 0);
});

test('a storage event for the key applies the new value in every document; other keys are ignored', () => {
  const storage = memoryStorage(JSON.stringify({ v: 1, theme: 'warm', mode: 'dark' }));
  const page = runPrepaint({ storage, top: topWith({}) });
  page.fire('storage', { key: 'revu.narration.mic', newValue: '{"theme":"aura"}' });
  assert.equal(page.shown().theme, 'warm');
  assert.equal(page.events.length, 0);
  // A browser fires the event after the write, so storage already holds the new value.
  const write = value => { storage.data.set(APPEARANCE_KEY, value); page.fire('storage', { key: APPEARANCE_KEY, newValue: value }); };
  write(JSON.stringify({ v: 1, theme: 'journal', mode: 'light' }));
  assert.deepEqual(page.shown(), { theme: 'journal', mode: 'light', pref: 'light', daypart: 'day' });
  assert.equal(page.events.length, 1);
  write('garbage');
  assert.deepEqual(page.shown(), { theme: 'warm', mode: 'dark', pref: 'dark', daypart: 'day' });
  // Two saves in one task: both events arrive after both writes, and the older one must not win.
  const theme = JSON.stringify({ v: 1, theme: 'neutral', mode: 'dark' }), mode = JSON.stringify({ v: 1, theme: 'neutral', mode: 'light' });
  storage.data.set(APPEARANCE_KEY, theme); storage.data.set(APPEARANCE_KEY, mode);
  page.fire('storage', { key: APPEARANCE_KEY, newValue: theme });
  assert.deepEqual(page.shown(), { theme: 'neutral', mode: 'light', pref: 'light', daypart: 'day' }, 'the current value, not the stale event');
  page.fire('storage', { key: APPEARANCE_KEY, newValue: mode });
  assert.equal(page.shown().mode, 'light');
  storage.data.set(APPEARANCE_KEY, JSON.stringify({ v: 1, theme: 'neutral', mode: 'dark' }));
  page.fire('storage', { key: null, newValue: null });
  assert.equal(page.shown().theme, 'neutral', 'storage.clear() or an unknown change reads storage again');
});

test('only the shell listens to Windows and the clock; frames get both through forwarding', () => {
  const matchMedia = fakeMedia(false);
  const shell = runPrepaint({ pathname: '/', stored: JSON.stringify({ v: 1, theme: 'aura', mode: 'system' }), matchMedia, hour: 16 });
  assert.deepEqual(shell.intervals.map(i => i.ms), [300000]);
  assert.deepEqual(matchMedia.listeners.map(([type]) => type), ['change']);
  assert.ok(matchMedia.queries.every(q => q === '(prefers-color-scheme: light)'));
  matchMedia.flip(true);
  assert.equal(shell.shown().mode, 'light');
  assert.equal(shell.events.length, 1);
  shell.clock.hour = 17;
  shell.intervals[0].fn();
  assert.equal(shell.shown().daypart, 'evening');
  assert.equal(shell.events.length, 2);
  shell.intervals[0].fn();
  assert.equal(shell.events.length, 2, 'an unchanged tick dispatches nothing');
  const frameMedia = fakeMedia(false);
  const frame = runPrepaint({ matchMedia: frameMedia, top: topWith({}) });
  assert.deepEqual(frame.intervals, []);
  assert.deepEqual(frameMedia.listeners, []);
  assert.deepEqual(Object.keys(frame.listeners), ['storage']);
  assert.deepEqual(Object.keys(shell.listeners), ['storage']);
});

test('the daypart boundaries are 05, 11, 17 and 21 in the prepaint and the module', () => {
  const expected = { 0: 'night', 4: 'night', 5: 'morning', 10: 'morning', 11: 'day', 16: 'day', 17: 'evening',
    20: 'evening', 21: 'night', 23: 'night' };
  for (const [hour, part] of Object.entries(expected)) assert.equal(daypartFor(Number(hour)), part, hour);
  for (let hour = 0; hour < 24; hour++) {
    assert.equal(runPrepaint({ hour, stored: '{"theme":"aura"}' }).shown().daypart, daypartFor(hour), String(hour));
  }
  assert.equal(daypartFor(29), 'morning');
  assert.equal(daypartFor(-1), 'night');
  assert.equal(daypartFor('x'), 'evening');
  assert.deepEqual(DAYPARTS, ['morning', 'day', 'evening', 'night']);
});

test('the prepaint repeats the module constants and stays a plain ES5 classic script', () => {
  assert.match(PREPAINT, new RegExp(`KEY = '${APPEARANCE_KEY}', DEFAULT_THEME = '${DEFAULT_THEME}', DEFAULT_MODE = '${DEFAULT_MODE}'`));
  assert.match(PREPAINT, new RegExp(`'${APPEARANCE_EVENT}'`));
  for (const theme of THEMES) {
    const page = runPrepaint({ stored: JSON.stringify({ theme, mode: 'dark' }) });
    assert.deepEqual(page.preloads(), FONT_PRELOADS[theme].map(name => `./fonts/${name}.woff2`), theme);
  }
  assert.doesNotMatch(PREPAINT, /^\s*(?:import|export)\b|=>|`|\b(?:let|const|class|async|await)\b|\.\.\./m);
  assert.doesNotThrow(() => new vm.Script(PREPAINT));
});

test('saveAppearance stores the choice and applies it through the shell, even when storage refuses it', () => {
  const calls = [], storage = memoryStorage();
  const shellApi = { reapply: next => calls.push(['top', next]) };
  const frameScope = { top: { __revuAppearance: shellApi }, __revuAppearance: { reapply: next => calls.push(['frame', next]) } };
  assert.deepEqual(saveAppearance({ theme: 'journal', mode: 'system' }, { storage, scope: frameScope }),
    { ok: true, applied: true, theme: 'journal', mode: 'system' });
  assert.deepEqual(storage.writes, [[APPEARANCE_KEY, '{"v":1,"theme":"journal","mode":"system"}']]);
  assert.deepEqual(calls, [['top', { theme: 'journal', mode: 'system' }]]);
  assert.deepEqual(saveAppearance({ theme: 'aura', mode: 'light' }, { storage: blockedStorage(), scope: frameScope }),
    { ok: false, applied: true, theme: 'aura', mode: 'light' });
  // A cross-origin or missing shell falls back to this document.
  const alone = { get top() { throw new Error('cross-origin'); }, __revuAppearance: { reapply: next => calls.push(['self', next]) } };
  assert.equal(saveAppearance({ theme: 'bogus' }, { storage, scope: alone }).applied, true);
  assert.deepEqual(calls.at(-1), ['self', { theme: 'warm', mode: 'dark' }]);
  assert.deepEqual(saveAppearance({ theme: 'neutral', mode: 'dark' }, { storage: null, scope: {} }),
    { ok: false, applied: false, theme: 'neutral', mode: 'dark' });
});

test('the shell carries the unsaved marker exactly while it shows a choice storage refused', () => {
  const shellRoot = fakeElement(), frameRoot = fakeElement(), seen = [];
  const shell = { document: { documentElement: shellRoot },
    __revuAppearance: { reapply: next => seen.push([next.theme, shellRoot.getAttribute(UNSAVED_ATTRIBUTE)]) } };
  const frameScope = { top: shell, document: { documentElement: frameRoot }, __revuAppearance: { reapply() {} } };
  assert.equal(saveAppearance({ theme: 'neutral', mode: 'light' }, { storage: blockedStorage(), scope: frameScope }).ok, false);
  assert.equal(shellRoot.getAttribute(UNSAVED_ATTRIBUTE), '');
  assert.deepEqual(seen, [['neutral', '']], 'marked before the shell applies it, so the mirror sees both together');
  assert.equal(frameRoot.getAttribute(UNSAVED_ATTRIBUTE), null, 'only the document that applies the choice is marked');
  assert.equal(saveAppearance({ theme: 'warm', mode: 'dark' }, { storage: memoryStorage(), scope: frameScope }).ok, true);
  assert.equal(shellRoot.getAttribute(UNSAVED_ATTRIBUTE), null, 'a saved choice clears it');
  // Without a reachable shell, the page that applies the choice carries the marker.
  const alone = { get top() { throw new Error('cross-origin'); }, document: { documentElement: frameRoot }, __revuAppearance: { reapply() {} } };
  saveAppearance({ theme: 'aura', mode: 'dark' }, { storage: blockedStorage(), scope: alone });
  assert.equal(frameRoot.getAttribute(UNSAVED_ATTRIBUTE), '');
  assert.equal(UNSAVED_ATTRIBUTE, 'data-appearance-unsaved');
});

test('readAppliedAppearance reads a document back with safe fallbacks', () => {
  const page = runPrepaint({ pathname: '/settings.html', stored: JSON.stringify({ theme: 'neutral', mode: 'system' }), light: true });
  const doc = { documentElement: page.root, location: { pathname: '/settings.html' } };
  assert.deepEqual(readAppliedAppearance(doc), { theme: 'neutral', mode: 'light', pref: 'system', daypart: 'day', page: 'settings' });
  const empty = readAppliedAppearance({ documentElement: fakeElement(), location: { pathname: '/' } });
  assert.deepEqual({ ...empty, daypart: undefined }, { theme: 'warm', mode: 'dark', pref: 'dark', daypart: undefined, page: 'index' });
  assert.ok(DAYPARTS.includes(empty.daypart));
  assert.equal(readAppliedAppearance(null).theme, 'warm');
});

test('a theme applied after parsing loads its faces through the font set, never a late preload link', async () => {
  const page = runPrepaint();
  assert.deepEqual(page.preloads(), ['./fonts/instrument-sans.woff2', './fonts/source-serif-4-roman.woff2']);
  const loads = [];
  page.win.document.readyState = 'complete';
  page.win.document.fonts = { load: font => { loads.push(font); return Promise.reject(new Error('offline')); } };
  page.api.reapply({ theme: 'neutral', mode: 'dark' });
  assert.deepEqual(loads, ['400 1em "Geist"']);
  assert.deepEqual(page.preloads(), ['./fonts/instrument-sans.woff2', './fonts/source-serif-4-roman.woff2'], 'no link after parsing');
  page.api.reapply({ theme: 'aura', mode: 'dark' });
  assert.deepEqual(loads, ['400 1em "Geist"'], 'a face is requested once per document');
  await new Promise(resolve => setImmediate(resolve)); // a rejected load is handled, not left unhandled
  // A document without a font set still applies the theme.
  const bare = runPrepaint();
  bare.win.document.readyState = 'interactive';
  assert.equal(bare.api.reapply({ theme: 'neutral', mode: 'light' }).theme, 'neutral');
});

test('the prepaint font families match FONT_PRELOADS and the faces appearance.css declares', async () => {
  const family = /var FAMILY = (\{[^}]*\});/.exec(PREPAINT);
  assert.ok(family, 'nav-prepaint.js declares FAMILY');
  const map = vm.runInNewContext(`(${family[1]})`);
  const css = await readFile(new URL('../ui/appearance.css', import.meta.url), 'utf8');
  const declared = new Map([...css.matchAll(/@font-face\{font-family:'([^']+)';src:url\('\.\/fonts\/([\w-]+)\.woff2'\)/g)].map(m => [m[2], m[1]]));
  const faces = [...new Set(Object.values(FONT_PRELOADS).flat())].sort();
  assert.deepEqual(Object.keys(map).sort(), faces);
  for (const face of faces) assert.equal(map[face], declared.get(face), face);
});
